using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class ProdStartedScenario
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        // PRODSTARTED has ten TAB-separated fields, without NumList or CR.
        string message = $"PRODSTARTED\t1003\t{time}\tLINE1\tNXT1\t1\t1\t0\tTESTJOB_T\t10002";
        byte[] frame = NeximInitializationProtocol.Frame(message);
        await stream.WriteAsync(frame, cancellationToken);
        Console.WriteLine($"TX PRODSTARTED: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string reply;
        try
        {
            reply = await new NeximFrameReader().ReadMessageAsync(stream, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout de 20 segundos esperando PRODSTARTED_ACK para SeqID=1003.", exception);
        }
        Console.WriteLine($"RX PRODSTARTED_ACK: STX {Visible(reply)} ETX");

        string[] names = ["EventName", "SeqID", "Result", "MachineName", "ModuleNo", "LaneNo", "ProductMode", "ProgramName", "PanelNo"];
        string[] expected = ["PRODSTARTED_ACK", "1003", "0", "NXT1", "1", "1", "0", "TESTJOB_T", "10002"];
        string[] actual = reply.Split('\t');
        if (actual.Length != expected.Length)
            throw new IOException($"PRODSTARTED_ACK inválido: se requieren 9 campos; recibidos {actual.Length}.");
        for (int index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                throw new IOException($"PRODSTARTED_ACK inválido: {names[index]} esperado '{expected[index]}', recibido '{Visible(actual[index])}'.");
        }
        Console.WriteLine("PRODSTARTED_ACK validado: SeqID=1003, Result=0 y todos los campos coinciden.");
    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
