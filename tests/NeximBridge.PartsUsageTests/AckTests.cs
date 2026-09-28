using System.Buffers.Binary;
using System.Text;
using NeximBridge;

internal static class AckTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string body)
    {
        foreach (int result in new[] { 0, 1 })
        {
            yield return ($"PARTSUSAGE_ACK Result={result}: exact fields and Big Endian framing", () =>
            {
                ParsedNeximMessage message = new NeximMessageParser().Parse(body);
                byte[] actual = new NeximAckBuilder().Build(message, result);
                string expected = $"PARTSUSAGE_ACK\t1001\t{result}\tNXT1\t1\t1\tTESTJOB_T\t10001";
                Check(BinaryPrimitives.ReadInt32BigEndian(actual) == Encoding.UTF8.GetByteCount(expected) + 2);
                Check(actual[4] == 0x02 && actual[^1] == 0x03);
                Check(Encoding.UTF8.GetString(actual, 5, actual.Length - 6) == expected);
                return Task.CompletedTask;
            });
            yield return ($"PARTSUSAGE_ACK Result={result}: existing stream, logging before write and FlushAsync", async () =>
            {
                AckLog log = new();
                using FlushStream stream = new(log);
                PartsUsageAckSender sender = new(new NeximAckBuilder(), log);
                await sender.SendAsync(stream, new NeximMessageParser().Parse(body), result);
                Check(stream.Flushed && stream.CanWrite);
                Check(log.Messages[0] == $"TX PARTSUSAGE_ACK SeqID=1001 Result={result} Machine=NXT1");
                Check(log.Messages[1].Contains($"STXPARTSUSAGE_ACK\\t1001\\t{result}\\tNXT1\\t1\\t1\\tTESTJOB_T\\t10001ETX"));
                Check(log.Messages[1].Contains("LengthBE="));
                Check(Encoding.UTF8.GetString(stream.ToArray()[5..^1]) == $"PARTSUSAGE_ACK\t1001\t{result}\tNXT1\t1\t1\tTESTJOB_T\t10001");
            });
        }
        yield return ("Negative ACK can echo header when NumList/detail parsing fails", () =>
        {
            var message = new NeximMessageParser().Parse(body.Replace("\t2\r", "\tINVALID\r"));
            byte[] frame = new NeximAckBuilder().Build(message, 1);
            Check(Encoding.UTF8.GetString(frame[5..^1]) == "PARTSUSAGE_ACK\t1001\t1\tNXT1\t1\t1\tTESTJOB_T\t10001");
            return Task.CompletedTask;
        });
        yield return ("Incomplete ACK identity is rejected without sending invented fields", async () =>
        {
            AckLog log = new(); using FlushStream stream = new(log);
            try
            {
                await new PartsUsageAckSender(new(), log).SendAsync(stream,
                    new NeximMessageParser().Parse("PARTSUSAGE\t1001"), 1);
            }
            catch (NeximAckException)
            {
                Check(stream.Length == 0 && !stream.Flushed);
                return;
            }
            throw new Exception("Missing expected exception");
        });
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("ACK assertion failed");
    }

    private sealed class AckLog : ILogger<PartsUsageAckSender>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class FlushStream(AckLog log) : MemoryStream
    {
        public bool Flushed { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Check(log.Messages.Count == 2);
            return base.WriteAsync(buffer, token);
        }
        public override Task FlushAsync(CancellationToken token)
        {
            Check(Length > 0);
            Flushed = true;
            return base.FlushAsync(token);
        }
    }
}
