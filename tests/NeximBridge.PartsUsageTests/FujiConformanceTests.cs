using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using NeximBridge;

internal static class FujiConformanceTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        foreach (string id in new[] { "1", "999999" })
            yield return ($"KEEPALIVE valid boundary {id}", async () => {
                using FlushStream stream = new();
                bool persisted = false;
                await new NeximKeepAliveAckBuilder().RespondAsync(stream, "KEEPALIVE\t" + id, token => {
                    Check(stream.ToArray().SequenceEqual(NeximInitializationProtocol.Frame("KEEPALIVE_ACK\t" + id)));
                    Check(token.CanBeCanceled && stream.Flushed); persisted = true; return Task.CompletedTask;
                }, NullLogger.Instance, default);
                Check(persisted);
            });
        foreach (string raw in new[] { "KEEPALIVE\t0", "KEEPALIVE\t1000000", "KEEPALIVE\tABC", "KEEPALIVE\t1\tEXTRA",
            "KEEPALIVE\t1\r", "KEEPALIVE\t1\n", "KEEPALIVE", "keepalive\t1", "KEEPALIVE\t+1", "KEEPALIVE\t 1" })
            yield return ($"KEEPALIVE rejects {raw.Replace("\r", "CR").Replace("\n", "LF")}", async () => {
                using MemoryStream stream = new();
                bool rejected = false;
                try { await new NeximKeepAliveAckBuilder().RespondAsync(stream, raw, _ => throw new Exception("Must not persist"), NullLogger.Instance, default); }
                catch (NeximMessageParseException) { rejected = true; }
                Check(rejected && stream.Length == 0);
            });
        yield return ("KEEPALIVE SQL failure leaves same TCP session available for next ACK", async () => {
            using TcpListener listener = new(IPAddress.Loopback, 0); listener.Start();
            using TcpClient peer = new();
            await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            for (int id = 1; id <= 2; id++)
            {
                await new NeximKeepAliveAckBuilder().RespondAsync(accepted.GetStream(), $"KEEPALIVE\t{id}",
                    _ => throw new IOException("SQL unavailable"), NullLogger.Instance, deadline.Token);
                Check(await new NeximFrameReader().ReadMessageAsync(peer.GetStream(), deadline.Token) == $"KEEPALIVE_ACK\t{id}");
            }
        });
        yield return ("KEEPALIVE persistence deadline bounded even when provider ignores cancellation", async () => {
            using MemoryStream stream = new(); TaskCompletionSource stalled = new();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await new NeximKeepAliveAckBuilder().RespondAsync(stream, "KEEPALIVE\t1", _ => stalled.Task, NullLogger.Instance, default)
                    .WaitAsync(TimeSpan.FromSeconds(8));
                Check(watch.Elapsed < TimeSpan.FromSeconds(8));
                Check(stream.ToArray().SequenceEqual(NeximInitializationProtocol.Frame("KEEPALIVE_ACK\t1")));
            }
            finally { stalled.TrySetResult(); }
        });

        string common = "\t1\t20260928103000\tLINE1\tNXT1\t1\t1";
        var examples = new (string Raw, Action<ParsedNeximMessage> Parse)[] {
            ("PARTSUSAGE" + common + "\tJOB\tPANEL\t1\r1\t5\tPART\tAVL\t100\t102\t2\t1\t1\t0\t0\t0\r", m => PartsUsageParser.Parse(m)),
            ("PDERROR" + common + "\tJOB\t1\r1\t5\tPART\tAVL\t1\t1\t1\t5\t1\tR1\t00000001\t00000010\r", m => PdErrorParser.Parse(m)),
            ("PRODSTARTED" + common + "\t0\tJOB\tPANEL", m => ProdStartedParser.Parse(m)),
            ("PRODCOMPLETEDII" + common + "\t0\tJOB\tPANEL\t12\t2\tA200\t45000", m => ProdCompletedIIParser.Parse(m)),
            ("PRODSTOPPED" + common + "\t0\tJOB\tPANEL\t5\t1", m => ProdStoppedParser.Parse(m)),
            ("UNLOADCOMP\t1\t20260928103000\tLINE1\tNXT1\t1\t1\r1\t5\tPART\tFEEDER\tREEL\t250\t120\r", m => UnloadCompParser.Parse(m))
        };
        foreach (var example in examples)
        {
            string name = example.Raw.Split('\t')[0];
            var ranges = new List<(int Record, int Field, int Min, int Max)>();
            if (name != "UNLOADCOMP") ranges.Add((0, 6, 1, 2));
            if (name.StartsWith("PROD")) ranges.Add((0, 7, 0, 1));
            if (name is "PRODCOMPLETEDII" or "PRODSTOPPED") { ranges.Add((0, 10, 0, 9999)); ranges.Add((0, 11, 0, 9999)); }
            if (name == "PDERROR") { ranges.Add((1, 6, 1, 4)); ranges.Add((1, 7, 1, 32)); }
            if (name == "UNLOADCOMP") ranges.Add((1, 6, 10, 999999));
            foreach (var range in ranges)
                foreach (int value in new[] { range.Min - 1, range.Min, range.Max, range.Max + 1 })
                    yield return ($"Fuji {name} record={range.Record} field={range.Field} value={value}", () => {
                        bool valid = value >= range.Min && value <= range.Max;
                        Parse(example.Parse, Change(example.Raw, range.Record, range.Field, value.ToString()), valid);
                        return Task.CompletedTask;
                    });
            if (name == "PRODCOMPLETEDII")
                foreach (string hex in new[] { "G", "Z", "A2G0", "Ｆ", "-1", "A 2", "0123456789abcdefABCDEF", "A", new string('F', 100) })
                    yield return ($"BSInfoBit ASCII hex {hex}", () => {
                        Parse(example.Parse, Change(example.Raw, 0, 12, hex), hex.All(Uri.IsHexDigit)); return Task.CompletedTask;
                    });
        }
        foreach (string value in new[] { "1", "999999", "0", "1000000", "ABC", "", "-1", "1.0", "999999999999999" })
            yield return ($"SQL SeqID conversion {value}", () => {
                object sql = NeximRepository.SqlSequence(value);
                Check(value is "1" or "999999" ? sql is int : sql is DBNull); return Task.CompletedTask;
            });
    }
    private static string Change(string raw, int record, int field, string value)
    {
        string[] records = raw.Split('\r'); string[] fields = records[record].Split('\t');
        fields[field] = value; records[record] = string.Join('\t', fields); return string.Join('\r', records);
    }
    private static void Parse(Action<ParsedNeximMessage> parse, string raw, bool valid)
    {
        bool accepted = true;
        try { parse(new NeximMessageParser().Parse(raw)); }
        catch (NeximMessageParseException) { accepted = false; }
        Check(accepted == valid);
    }
    private static void Check(bool value) { if (!value) throw new Exception("Conformance assertion failed"); }
    private sealed class FlushStream : MemoryStream
    {
        public bool Flushed;
        public override Task FlushAsync(CancellationToken token)
        { Flushed = true; return base.FlushAsync(token); }
    }
}
