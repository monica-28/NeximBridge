using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class PdErrorScenario
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        // Fuji 4.52.1: no PanelNo; NumList and both records end with CR.
        string message = $"PDERROR\t1002\t{time}\tLINE1\tNXT1\t1\t1\tTESTJOB_T\t2\r" +
            "1\t5\tRES-10K\tRES-10K-AVL\t1\t1\t1\t5\t1\tR101\t00000001\t00000010\r" +
            "1\t6\tCAP-100N\tCAP-100N-AVL\t2\t2\t2\t6\t1\tC202\t00000002\t00000020\r";
        byte[] frame = NeximInitializationProtocol.Frame(message);
        await stream.WriteAsync(frame, cancellationToken);
        Console.WriteLine($"TX PDERROR: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string reply;
        try
        {
            reply = await new NeximFrameReader().ReadMessageAsync(stream, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout de 20 segundos esperando PDERROR_ACK para SeqID=1002.", exception);
        }
        Console.WriteLine($"RX PDERROR_ACK: STX {Visible(reply)} ETX");

        string[] names = ["EventName", "SeqID", "Result", "MachineName", "ModuleNo", "LaneNo", "ProgramName"];
        string[] expected = ["PDERROR_ACK", "1002", "0", "NXT1", "1", "1", "TESTJOB_T"];
        string[] actual = reply.Split('\t');
        if (actual.Length != expected.Length)
            throw new IOException($"PDERROR_ACK inválido: se requieren 7 campos; recibidos {actual.Length}.");
        for (int index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                throw new IOException($"PDERROR_ACK inválido: {names[index]} esperado '{expected[index]}', recibido '{Visible(actual[index])}'.");
        }
        Console.WriteLine("PDERROR_ACK validado: SeqID=1002, Result=0 y todos los campos coinciden.");
    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
