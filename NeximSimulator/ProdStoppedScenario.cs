using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class ProdStoppedScenario
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        // PRODSTOPPED has twelve TAB-separated fields, without NumList or CR.
        string message = $"PRODSTOPPED\t1005\t{time}\tLINE1\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1";
        byte[] frame = NeximInitializationProtocol.Frame(message);
        await stream.WriteAsync(frame, cancellationToken);
        Console.WriteLine($"TX PRODSTOPPED: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string reply;
        try
        {
            reply = await new NeximFrameReader().ReadMessageAsync(stream, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout de 20 segundos esperando PRODSTOPPED_ACK para SeqID=1005.", exception);
        }
        Console.WriteLine($"RX PRODSTOPPED_ACK: STX {Visible(reply)} ETX");

        string[] names = ["EventName", "SeqID", "Result", "MachineName", "ModuleNo", "LaneNo", "ProductMode", "ProgramName", "PanelNo", "BlockCount", "BlockSkipCount"];
        string[] expected = ["PRODSTOPPED_ACK", "1005", "0", "NXT1", "1", "1", "0", "TESTJOB_T", "10003", "5", "1"];
        string[] actual = reply.Split('\t');
        if (actual.Length != expected.Length)
            throw new IOException($"PRODSTOPPED_ACK inválido: se requieren 11 campos; recibidos {actual.Length}.");
        for (int index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                throw new IOException($"PRODSTOPPED_ACK inválido: {names[index]} esperado '{expected[index]}', recibido '{Visible(actual[index])}'.");
        }
        Console.WriteLine("PRODSTOPPED_ACK validado: SeqID=1005, Result=0 y todos los campos coinciden.");
    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
