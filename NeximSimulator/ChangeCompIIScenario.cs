using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class ChangeCompIIScenario
{
    public static async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        string time = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string message = $"CHANGECOMPII\t1007\t{time}\tLINE1\tNXT1\t1\t1\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\tREEL-C\t4500\r";
        byte[] frame = NeximInitializationProtocol.Frame(message);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        Console.WriteLine($"TX CHANGECOMPII: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");

    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
