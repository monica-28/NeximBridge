using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class LoadCompIVScenario
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string message = $"LOADCOMPIV\t1006\t{time}\tLINE1\tNXT1\t1\t2\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\tVENDOR-A\tLOT-A\t202609\tLIGHT-A\t5000\t0\r1\t6\tCAP-100N\tFEEDER-006\tREEL-B\tVENDOR-B\tLOT-B\t202609\tLIGHT-B\t3000\t0\r";
        byte[] frame = NeximInitializationProtocol.Frame(message);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Console.WriteLine($"TX LOADCOMPIV: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");

    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
