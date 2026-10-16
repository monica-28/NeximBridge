using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class McAlarmOffScenario
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string message = $"MCALARMOFF\t1010\t{time}\tLINE1\tNXT1\t1\t00000001\t00000010";
        byte[] frame = NeximInitializationProtocol.Frame(message);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Console.WriteLine($"TX MCALARMOFF: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string reply;
        try
        {
            reply = await new NeximFrameReader().ReadMessageAsync(stream, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout de 20 segundos esperando MCALARMOFF_ACK para SeqID=1010.", exception);
        }
        Console.WriteLine($"RX MCALARMOFF_ACK: STX {Visible(reply)} ETX");

        string[] names = ["EventName", "SeqID", "Result", "MachineName", "ModuleNo"];
        string[] expected = ["MCALARMOFF_ACK", "1010", "0", "NXT1", "1"];
        string[] actual = reply.Split('\t');
        if (actual.Length != expected.Length)
            throw new IOException($"MCALARMOFF_ACK inválido: se requieren 5 campos; recibidos {actual.Length}.");
        for (int index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                throw new IOException($"MCALARMOFF_ACK inválido: {names[index]} esperado '{expected[index]}', recibido '{Visible(actual[index])}'.");
        }
        Console.WriteLine("MCALARMOFF_ACK validado: SeqID=1010, Result=0 y todos los campos coinciden.");
    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
