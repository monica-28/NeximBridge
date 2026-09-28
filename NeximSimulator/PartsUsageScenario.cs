using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class PartsUsageScenario
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        // Fuji 4.32.1, pp. 104-105: NumList followed by CR-terminated records.
        string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string message = $"PARTSUSAGE\t1001\t{time}\tLINE1\tNXT1\t1\t1\tTESTJOB_T\t10001\t2\r" +
            // StageNo, SlotNo, PartName, AVLName, PartsUsage, PickupCount, ErrorParts,
            // ErrorReject, RejectParts, DislodgedParts, Rescancount, NoPickup.
            "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0\r" +
            "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0\r";
        byte[] frame = NeximInitializationProtocol.Frame(message);
        await stream.WriteAsync(frame, cancellationToken);
        Console.WriteLine($"TX PARTSUSAGE: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20)); // Fuji 4.32 default ACK timeout.
        string reply;
        try
        {
            reply = await new NeximFrameReader().ReadMessageAsync(stream, deadline.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timeout de 20 segundos esperando PARTSUSAGE_ACK para SeqID=1001.", exception);
        }
        Console.WriteLine($"RX: STX {Visible(reply)} ETX");

        // Fuji 4.32.2, pp. 105-106. Result precedes MachineName in this ACK.
        string[] names = ["EventName", "SeqID", "Result", "MachineName", "ModuleNo", "LaneNo", "ProgramName", "PanelNo"];
        string[] expected = ["PARTSUSAGE_ACK", "1001", "0", "NXT1", "1", "1", "TESTJOB_T", "10001"];
        string[] actual = reply.Split('\t');
        if (actual.Length != expected.Length)
            throw new IOException($"PARTSUSAGE_ACK inválido: se requieren 8 campos; recibidos {actual.Length}.");
        for (int index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
                throw new IOException($"PARTSUSAGE_ACK inválido: {names[index]} esperado '{expected[index]}', recibido '{Visible(actual[index])}'.");
        }
        Console.WriteLine("PARTSUSAGE_ACK validado: SeqID=1001, Result=0 y todos los campos coinciden. Evento único completado.");
    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
