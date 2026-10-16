using System.Globalization;
using NeximBridge;

namespace NeximSimulator;

internal static class DedupTestScenario
{
    public static async Task RunAsync(Stream stream, IReadOnlyDictionary<string, bool> events,
        CancellationToken cancellationToken)
    {
        if (!events.TryGetValue("PARTSUSAGE", out bool partsAck) || !partsAck)
            throw new IOException("--dedup-test requiere PARTSUSAGE con ACK=1.");
        if (!events.TryGetValue("LOADCOMPIV", out bool loadAck) || loadAck)
            throw new IOException("--dedup-test requiere LOADCOMPIV con ACK=0.");

        string partsTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string partsMessage = $"PARTSUSAGE\t2001\t{partsTime}\tLINE1\tNXT1\t1\t1\tDEDUP_TEST\tDEDUP001\t2\r" +
            "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0\r" +
            "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0\r";
        byte[] partsFrame = NeximInitializationProtocol.Frame(partsMessage);
        await Send(partsFrame, partsMessage, "PARTSUSAGE ORIGINAL");
        await ReceivePartsAck("ORIGINAL");
        // Write the very same frame buffer again, without rebuilding Time or payload.
        await Send(partsFrame, partsMessage, "PARTSUSAGE DUPLICATE");
        await ReceivePartsAck("DUPLICATE");

        string loadTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string loadMessage = $"LOADCOMPIV\t2002\t{loadTime}\tLINE1\tNXT1\t1\t2\r" +
            "1\t15\tDEDUP-RES\tFEEDER-D15\tREEL-D15\tVENDOR-D\tLOT-D15\t202610\tLIGHT-D\t9000\t0\r" +
            "1\t16\tDEDUP-CAP\tFEEDER-D16\tREEL-D16\tVENDOR-D\tLOT-D16\t202610\tLIGHT-D\t8000\t0\r";
        byte[] loadFrame = NeximInitializationProtocol.Frame(loadMessage);
        await Send(loadFrame, loadMessage, "LOADCOMPIV ORIGINAL");
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        await Send(loadFrame, loadMessage, "LOADCOMPIV DUPLICATE");
        // Monitoring only: no ACK read. Allow the client time to finish persistence.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        Console.WriteLine("--dedup-test completado: cuatro transmisiones realizadas; cierre de conexión.");

        async Task Send(byte[] frame, string message, string label)
        {
            Console.WriteLine($"TX {label}: LengthBE={Convert.ToHexString(frame.AsSpan(0, 4))} STX {Visible(message)} ETX");
            await stream.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        async Task ReceivePartsAck(string label)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            string reply;
            try { reply = await new NeximFrameReader().ReadMessageAsync(stream, deadline.Token); }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timeout de 20 segundos esperando PARTSUSAGE_ACK {label}, SeqID=2001.", error);
            }
            Console.WriteLine($"RX PARTSUSAGE_ACK {label}: STX {Visible(reply)} ETX");
            const string expected = "PARTSUSAGE_ACK\t2001\t0\tNXT1\t1\t1\tDEDUP_TEST\tDEDUP001";
            if (!string.Equals(reply, expected, StringComparison.Ordinal))
                throw new IOException($"PARTSUSAGE_ACK {label} inválido: deben coincidir los ocho campos y Result=0.");
        }
    }

    private static string Visible(string message) => message.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
}
