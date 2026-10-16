using System.Globalization;
using System.Net.Sockets;
using NeximBridge;

namespace NeximSimulator;

internal static class ReconnectDedupTestScenario
{
    public static async Task RunAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        string message = string.Empty;
        byte[] frame = Array.Empty<byte>();
        for (int session = 1; session <= 2; session++)
        {
            using (TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken))
            {
                await using NetworkStream stream = client.GetStream();
                SimulatorInitialization initialization = new();
                await initialization.RunAsync(stream, TimeSpan.FromSeconds(10), cancellationToken);
                if (!initialization.Events.TryGetValue("PARTSUSAGE", out bool ack) || !ack)
                    throw new IOException("--reconnect-dedup-test requiere PARTSUSAGE con ACK=1.");
                Console.WriteLine($"SESSION {session} INITIALIZATION COMPLETED");

                if (session == 1)
                {
                    string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                    message = $"PARTSUSAGE\t3001\t{time}\tLINE1\tNXT1\t1\t1\tRECONNECT_DEDUP\tRECON001\t2\r" +
                        "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0\r" +
                        "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0\r";
                    frame = NeximInitializationProtocol.Frame(message);
                }

                string label = session == 1 ? "PARTSUSAGE" : "EXACT DUPLICATE";
                Console.WriteLine($"SESSION {session} TX {label}: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");
                // Retain and write the same buffer across both TCP sessions.
                await stream.WriteAsync(frame, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                await ReceiveAck(stream, session, cancellationToken);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            if (session == 1)
                Console.WriteLine("CONNECTION CLOSED");
        }
        Console.WriteLine("RECONNECT DEDUP TEST PASSED");
    }

    private static async Task ReceiveAck(Stream stream, int session, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string reply;
        try { reply = await new NeximFrameReader().ReadMessageAsync(stream, deadline.Token); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout de 20 segundos esperando PARTSUSAGE_ACK en SESSION {session}, SeqID=3001.", error);
        }
        Console.WriteLine($"SESSION {session} RX ACK: STX {Visible(reply)} ETX");
        const string expected = "PARTSUSAGE_ACK\t3001\t0\tNXT1\t1\t1\tRECONNECT_DEDUP\tRECON001";
        if (!string.Equals(reply, expected, StringComparison.Ordinal))
            throw new IOException("PARTSUSAGE_ACK inválido: deben coincidir los ocho campos y Result=0.");
    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
