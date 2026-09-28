using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class KeepAliveSession
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NeximFrameReader reader = new();
        Task<string>? receive = null;
        int sequence = 1004; // 1004 = PRODCOMPLETEDII; first KEEPALIVE = 1005.
        try
        {
            while (!session.IsCancellationRequested)
            {
                // One reader remains active even between KEEPALIVEs so a client
                // disconnect is observed immediately, without closing a healthy session.
                receive = reader.ReadMessageAsync(stream, session.Token);
                Task interval = Task.Delay(TimeSpan.FromSeconds(120), session.Token);
                if (await Task.WhenAny(receive, interval) == receive)
                {
                    string unexpected = await receive;
                    throw new IOException($"Mensaje inesperado sin KEEPALIVE pendiente: {unexpected.Replace("\t", "\\t")}");
                }
                await interval;
                sequence = sequence == 999999 ? 1 : sequence + 1;
                string seqId = sequence.ToString(CultureInfo.InvariantCulture);
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await stream.WriteAsync(NeximInitializationProtocol.Frame($"KEEPALIVE\t{seqId}"), deadline.Token);
                    Console.WriteLine($"KEEPALIVE enviado: SeqID={seqId}");
                    string reply = await receive.WaitAsync(deadline.Token);
                    Console.WriteLine($"KEEPALIVE_ACK recibido (pendiente de validar): {reply.Replace("\t", "\\t")}");
                    if (reply != $"KEEPALIVE_ACK\t{seqId}")
                        throw new IOException($"KEEPALIVE_ACK inválido: se esperaba KEEPALIVE_ACK y SeqID={seqId}, sin campos adicionales.");
                    Console.WriteLine($"KEEPALIVE_ACK validado: SeqID={seqId}. Conexión mantenida; estado: recepción.");
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timeout de 30 segundos esperando KEEPALIVE_ACK con SeqID={seqId}.", exception);
                }
            }
        }
        finally
        {
            session.Cancel();
            // Finish the only pending read before its owning caller disposes the stream.
            if (receive is not null)
            {
                try { await receive; }
                catch (Exception) { /* The session's original error is reported by Program. */ }
            }
        }
    }
}
