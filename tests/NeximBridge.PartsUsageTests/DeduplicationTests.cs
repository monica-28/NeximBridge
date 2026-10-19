using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NeximBridge;

internal static class DeduplicationTests
{
    private static readonly Dictionary<string, string> Bodies = new()
    {
        ["PARTSUSAGE"] = "PARTSUSAGE\t1001\t20260928103000\tLINE1\tNXT1\t1\t1\tTESTJOB_T\t10001\t1\r1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0\r",
        ["PDERROR"] = "PDERROR\t1002\t20260928103000\tLINE1\tNXT1\t1\t1\tTESTJOB_T\t1\r1\t5\tRES-10K\tAVL\t1\t1\t1\t5\t1\tR101\t00000001\t00000010\r",
        ["PRODSTARTED"] = "PRODSTARTED\t1003\t20260928103000\tLINE1\tNXT1\t1\t1\t0\tTESTJOB_T\t10002",
        ["PRODCOMPLETEDII"] = "PRODCOMPLETEDII\t1004\t20260928103000\tLINE1\tNXT1\t1\t1\t0\tTESTJOB_T\t10002\t12\t2\tA200\t45000",
        ["PRODSTOPPED"] = "PRODSTOPPED\t1005\t20260928103000\tLINE1\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1",
        ["LOADCOMPIV"] = "LOADCOMPIV\t1006\t20260928103000\tLINE1\tNXT1\t1\t1\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\tVENDOR-A\tLOT-A\t202609\tLIGHT-A\t5000\t0\r",
        ["CHANGECOMPII"] = "CHANGECOMPII\t1007\t20260928103000\tLINE1\tNXT1\t1\t1\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\tREEL-C\t4500\r",
        ["UNLOADCOMP"] = "UNLOADCOMP\t1008\t20260928103000\tLINE1\tNXT1\t1\t1\r1\t5\tRES-10K\tFEEDER-005\tREEL-A\t250\t120\r",
        ["MCALARMON"] = "MCALARMON\t1009\t20260928103000\tLINE1\tNXT1\t1\t00000001\t00000010",
        ["MCALARMOFF"] = "MCALARMOFF\t1010\t20260928103000\tLINE1\tNXT1\t1\t00000001\t00000010"
    };

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        foreach (bool requiresAck in new[] { true, false })
            yield return ($"Retransmission across TCP reconnection Ack={requiresAck}: one RAW, one normalized batch", async () =>
            {
                string body = Bodies[requiresAck ? "PARTSUSAGE" : "LOADCOMPIV"];
                Database db = new(); Repository repo = new(db);
                PartsStore parts = new(db, false); LoadStore load = new(db);
                using TcpListener listener = ConnectionTests.Listen();
                var config = ConnectionTests.Config(ConnectionTests.Port(listener));
                using NeximTcpClientService tcp = new(config, NullLogger<NeximTcpClientService>.Instance);
                TestLog processingLog = new(); var processor = new NeximEventProcessor(repo, processingLog);
                using Worker worker = ConnectionTests.CreateWorker(tcp, config, NullLogger<Worker>.Instance, processor,
                    new(parts, NullLogger<PartsUsageHandler>.Instance), new(load, NullLogger<LoadCompIVHandler>.Instance));
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
                await tcp.StartAsync(default); await worker.StartAsync(default);
                try
                {
                    using (TcpClient first = await listener.AcceptTcpClientAsync(deadline.Token))
                    {
                        await ConnectionTests.Handshake(first.GetStream(), deadline.Token);
                        await first.GetStream().WriteAsync(NeximInitializationProtocol.Frame(body), deadline.Token);
                        await ConnectionTests.Until(() => db.Rows.Count == 1 && db.Rows[0].Status == "PROCESSED", deadline.Token);
                        // Discard the first ACK rather than relying on its arrival; persistence is already complete.
                    }
                    using TcpClient second = await listener.AcceptTcpClientAsync(deadline.Token);
                    await ConnectionTests.Handshake(second.GetStream(), deadline.Token);
                    await second.GetStream().WriteAsync(NeximInitializationProtocol.Frame(body), deadline.Token);
                    if (requiresAck)
                    {
                        string ack = await new NeximFrameReader().ReadMessageAsync(second.GetStream(), deadline.Token);
                        byte[] expected = new NeximAckBuilder().Build(Message(body));
                        Equal(Encoding.UTF8.GetString(expected, 5, expected.Length - 6), ack);
                    }
                    else
                    {
                        await ConnectionTests.Until(() => db.Operations.Count(op => op == "SELECT") == 1, deadline.Token);
                        using CancellationTokenSource noAck = new(TimeSpan.FromMilliseconds(200));
                        Check(await Failure(new NeximFrameReader().ReadMessageAsync(second.GetStream(), noAck.Token)) is OperationCanceledException,
                            "Monitoring event sent an ACK or closed session");
                    }
                    Equal(1, db.Rows.Count); Equal(body, db.Rows[0].Raw); Equal("PROCESSED", db.Rows[0].Status);
                    Equal(1, requiresAck ? parts.Details.Count : load.Details);
                    Check(processingLog.Lines.Any(line => line.Text.StartsWith("Duplicate event ignored")), "Not detected as duplicate");
                }
                finally { await worker.StopAsync(deadline.Token); await tcp.StopAsync(deadline.Token); }
            });
        yield return ("Malformed SeqID preserves exact RAW with SQL NULL", async () =>
        {
            Database db = new(); using Connection connection = new(db);
            const string raw = "PARTSUSAGE\tABC\tunaltered raw\r";
            await NeximRepository.InsertRawEventOrGetExistingAsync(connection, "PARTSUSAGE", "ABC", raw, DateTimeOffset.UtcNow);
            Equal(raw, db.Rows.Single().Raw);
            Check(db.Rows.Single().Seq is null, "Malformed SeqID must be SQL NULL");
        });
        yield return ("Hash: SHA256 exact UTF-8, 32 bytes and stable", () =>
        {
            const string raw = " MCALARMON\t1\rMáquina\r\n ";
            byte[] hash = RawEventDeduplication.ComputeHash("MCALARMON", raw)!;
            Equal(32, hash.Length);
            Bytes(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), hash);
            Bytes(hash, RawEventDeduplication.ComputeHash("MCALARMON", raw)!);
            return Task.CompletedTask;
        });
        foreach (string changed in new[] { "X\tY", "X\tY\n", "X Y\r", " X\tY\r", "X\tY\r ", "x\tY\r", "X\tY\r\n" })
            yield return ($"Hash: no trimming/case/TAB/CR normalization [{changed.Replace("\r", "CR").Replace("\n", "LF")} ]", () =>
            {
                Check(!RawEventDeduplication.ComputeHash("PARTSUSAGE", "X\tY\r")!
                    .SequenceEqual(RawEventDeduplication.ComputeHash("PARTSUSAGE", changed)!), "Different payload collapsed");
                return Task.CompletedTask;
            });
        foreach (string control in new[] { "KEEPALIVE", "SETEV", "STARTEV", "SETEV_ACK", "RECOVERYUP", "PCBCHECKOUTVII" })
            yield return ($"Hash: {control} excluded, repeated control inserts keep NULL hash", async () =>
            {
                Check(!RawEventDeduplication.Supports(control), "Control deduplicated");
                Check(RawEventDeduplication.ComputeHash(control, control + "\t1") is null, "Control hash is not NULL");
                Database db = new(); Repository repo = new(db);
                var first = await repo.InsertRawEventOrGetExistingAsync(control, "1", control + "\t1", DateTimeOffset.UtcNow);
                var second = await repo.InsertRawEventOrGetExistingAsync(control, "1", control + "\t1", DateTimeOffset.UtcNow);
                Check(!first.IsDuplicate && !second.IsDuplicate && first.RawEventId != second.RawEventId, "Control blocked by hash");
                Check(db.Rows.All(row => row.Hash is null), "Control stored hash");
            });
        foreach (int number in new[] { 2601, 2627 })
            yield return ($"Repository: second INSERT throws actual SqlException {number}, then SELECT returns same RAW", async () =>
            {
                Database db = new() { DuplicateErrorNumber = number }; Repository repo = new(db);
                var message = Message(Bodies["PARTSUSAGE"]);
                var first = await Insert(repo, message);
                Check(!first.IsDuplicate && first.ParseStatus == "PENDING", "First insert result");
                db.Rows[0].Status = "PROCESSED";
                var second = await Insert(repo, message);
                Check(second.IsDuplicate, "Duplicate not detected"); Equal(first.RawEventId, second.RawEventId);
                Equal("PROCESSED", second.ParseStatus); Equal(message.RawMessage, second.RawMessage);
                Equal(message.EventName, second.EventName); Equal(message.SeqID, second.SeqId);
                Equal(1, db.Rows.Count); Equal("INSERT,INSERT,SELECT", string.Join(',', db.Operations));
                Equal(number, db.LastException!.Number);
                Bytes(SHA256.HashData(Encoding.UTF8.GetBytes(message.RawMessage)), db.Rows[0].Hash!);
            });
        foreach (int number in new[] { -2, 1205, 547 })
            yield return ($"Repository: SqlException {number} propagated without SELECT", async () =>
            {
                SqlException expected = SqlError(number);
                Database db = new() { InsertError = expected };
                var error = await Failure(Insert(new(db), Message(Bodies["MCALARMON"])));
                Check(ReferenceEquals(expected, error), "Exception replaced"); Equal("INSERT", string.Join(',', db.Operations));
            });
        foreach (int number in new[] { 2601, 2627 })
            yield return ($"Repository: {number} without matching hash is not a duplicate", async () =>
            {
                SqlException expected = SqlError(number); Database db = new() { InsertError = expected };
                var error = await Failure(Insert(new(db), Message(Bodies["MCALARMON"])));
                Check(ReferenceEquals(expected, error), "Unrelated unique constraint hidden");
                Equal("INSERT,SELECT", string.Join(',', db.Operations));
            });
        yield return ("Repository: hash match with different payload is rejected without mutation", async () =>
        {
            Database db = new(); Repository repo = new(db); var message = Message(Bodies["MCALARMON"]);
            await Insert(repo, message); db.Rows[0].Raw = "different payload";
            Check(await Failure(Insert(repo, message)) is InvalidOperationException, "Hash collision accepted");
            Equal("different payload", db.Rows[0].Raw); Equal("PENDING", db.Rows[0].Status);
        });
        yield return ("Repository: historical NULL hashes are neither backfilled nor matched", async () =>
        {
            Database db = new(); var message = Message(Bodies["MCALARMON"]);
            db.Rows.Add(new(1, message.EventName, message.SeqID, message.RawMessage, null) { Status = "PROCESSED" });
            var inserted = await Insert(new(db), message);
            Check(!inserted.IsDuplicate && inserted.RawEventId == 2, "Historical RAW reused");
            Check(db.Rows[0].Hash is null, "Historical hash backfilled"); Equal("PROCESSED", db.Rows[0].Status);
        });
        yield return ("Repository: 20 concurrent same-hash INSERT attempts produce exactly one new RAW", async () =>
        {
            Database db = new(); Repository repo = new(db); var message = Message(Bodies["MCALARMON"]);
            var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => Insert(repo, message))));
            Equal(1, results.Count(result => !result.IsDuplicate)); Equal(1, db.Rows.Count);
            Check(results.All(result => result.RawEventId == 1), "Different ids");
            Equal(20, db.Operations.Count(op => op == "INSERT")); Equal(19, db.Operations.Count(op => op == "SELECT"));
        });

        foreach (var (name, body) in Bodies)
        {
            bool supportedAck = name is not ("LOADCOMPIV" or "CHANGECOMPII" or "UNLOADCOMP");
            yield return ($"{name}: first event processes once; exact retransmission never replays handler", async () =>
            {
                Database db = new(); Repository repo = new(db); TestLog log = new(); NeximEventProcessor processor = new(repo, log);
                int normalizedRows = 0; var message = Message(body);
                Task Handle(ParsedNeximMessage current, long id, CancellationToken token)
                { Equal(body, current.RawMessage); normalizedRows++; db.Rows.Single(row => row.Id == id).Status = "PROCESSED"; return Task.CompletedTask; }
                using MemoryStream firstAck = new(), secondAck = new();
                await processor.ProcessAsync(firstAck, message, supportedAck, Handle, Sender);
                await processor.ProcessAsync(secondAck, message, supportedAck, Handle, Sender);
                Equal(1, normalizedRows); Equal(1, db.Rows.Count); Equal("PROCESSED", db.Rows[0].Status);
                if (supportedAck) { Bytes(new NeximAckBuilder().Build(message), firstAck.ToArray()); Bytes(firstAck.ToArray(), secondAck.ToArray()); }
                else { Equal(0L, firstAck.Length); Equal(0L, secondAck.Length); }
                Check(log.Lines.Any(line => line.Text.StartsWith("RAW event inserted")), "Missing new log");
                Check(log.Lines.Any(line => line.Text.StartsWith("Duplicate event ignored")), "Missing duplicate log");
                Check(log.Lines.All(line => !line.Text.Contains(body, StringComparison.Ordinal)), "Full raw leaked in log");
            });
            foreach (string status in new[] { "PROCESSED", "PENDING", "ERROR", "UNKNOWN" })
            foreach (bool ack in supportedAck ? new[] { true, false } : new[] { false })
                yield return ($"{name}: duplicate {status} Ack={ack} leaves RAW and normalized data untouched", async () =>
                {
                    Database db = new(); Repository repo = new(db); var message = Message(body); await Insert(repo, message);
                    db.Rows[0].Status = status; TestLog log = new(); using MemoryStream stream = new();
                    await new NeximEventProcessor(repo, log).ProcessAsync(stream, message, ack,
                        (_, _, _) => throw new Exception("Handler replayed"), Sender);
                    Equal(1, db.Rows.Count); Equal(body, db.Rows[0].Raw); Equal(status, db.Rows[0].Status);
                    if (ack) Bytes(new NeximAckBuilder().Build(message, status == "PROCESSED" ? 0 : 1), stream.ToArray());
                    else Equal(0L, stream.Length);
                    Equal(status == "PROCESSED" ? LogLevel.Information : LogLevel.Warning, log.Lines.Single().Level);
                    Check(log.Lines[0].Text.Contains("RawEventId=1"), "Existing id missing from log");
                    Equal(0, repo.LegacyCalls);
                });
            if (supportedAck)
                foreach (string status in new[] { "PROCESSED", "PENDING", "ERROR" })
                    yield return ($"{name}: lost duplicate ACK ({status}) never updates RAW or sends another ACK", async () =>
                    {
                        Database db = new(); Repository repo = new(db); var message = Message(body); await Insert(repo, message);
                        db.Rows[0].Status = status; using BrokenStream stream = new();
                        Check(await Failure(new NeximEventProcessor(repo, new TestLog()).ProcessAsync(stream, message, true,
                            (_, _, _) => throw new Exception("Handler replayed"), Sender)) is IOException, "Wrong send failure");
                        Equal(1, stream.Writes); Equal(status, db.Rows[0].Status); Equal(1, db.Rows.Count);
                    });
            yield return ($"{name}: same EventName/SeqID but different Time processes both", async () =>
            {
                Database db = new(); Repository repo = new(db); int handled = 0;
                NeximEventProcessor processor = new(repo, new TestLog()); using MemoryStream stream = new();
                Task Handle(ParsedNeximMessage current, long id, CancellationToken token)
                { handled++; db.Rows.Single(row => row.Id == id).Status = "PROCESSED"; return Task.CompletedTask; }
                await processor.ProcessAsync(stream, Message(body), false, Handle, Sender);
                await processor.ProcessAsync(stream, Message(body.Replace("20260928103000", "20260928103001")), false, Handle, Sender);
                Equal(2, handled); Equal(2, db.Rows.Count); Check(!db.Rows[0].Hash!.SequenceEqual(db.Rows[1].Hash!), "Hashes equal");
            });
        }
        yield return ("PARTSUSAGE: quantity/counter change creates distinct RAW despite same SeqID", async () =>
        {
            Database db = new(); Repository repo = new(db);
            await Insert(repo, Message(Bodies["PARTSUSAGE"]));
            var result = await Insert(repo, Message(Bodies["PARTSUSAGE"].Replace("\t100\t102\t", "\t101\t102\t")));
            Check(!result.IsDuplicate, "Counter change collapsed"); Equal(2, db.Rows.Count);
        });
        yield return ("Concurrent retransmission during handler returns Result=1; later retry returns Result=0", async () =>
        {
            Database db = new(); Repository repo = new(db); var message = Message(Bodies["MCALARMON"]);
            NeximEventProcessor processor = new(repo, new TestLog());
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously), release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int handled = 0;
            async Task Handle(ParsedNeximMessage current, long id, CancellationToken token)
            { handled++; entered.SetResult(); await release.Task; db.Rows.Single(row => row.Id == id).Status = "PROCESSED"; }
            using MemoryStream first = new(), pending = new(), processed = new();
            Task owner = processor.ProcessAsync(first, message, true, Handle, Sender);
            await entered.Task;
            try
            {
                await processor.ProcessAsync(pending, message, true, Handle, Sender);
                Bytes(new NeximAckBuilder().Build(message, 1), pending.ToArray()); Equal("PENDING", db.Rows[0].Status);
            }
            finally { release.SetResult(); }
            await owner;
            await processor.ProcessAsync(processed, message, true, Handle, Sender);
            Bytes(new NeximAckBuilder().Build(message, 0), processed.ToArray()); Equal(1, handled); Equal(1, db.Rows.Count);
        });
        yield return ("Duplicate ACK uses current SeqID text, even if SQL seq_id loses leading zeroes", async () =>
        {
            Database db = new(); Repository repo = new(db); var message = Message(Bodies["MCALARMON"].Replace("1009", "001009"));
            await Insert(repo, message); db.Rows[0].Status = "PROCESSED"; db.Rows[0].Seq = "1009";
            using MemoryStream stream = new();
            await new NeximEventProcessor(repo, new TestLog()).ProcessAsync(stream, message, true,
                (_, _, _) => throw new Exception("Handler replayed"), Sender);
            Bytes(new NeximAckBuilder().Build(message), stream.ToArray());
        });
        yield return ("KEEPALIVE cannot enter the deduplicating event processor", async () =>
        {
            Database db = new(); using MemoryStream stream = new();
            Check(await Failure(new NeximEventProcessor(new Repository(db), new TestLog()).ProcessAsync(stream,
                Message("KEEPALIVE\t1001"), true, (_, _, _) => throw new Exception("Control handled"), Sender)) is ArgumentException, "Control accepted");
            Equal(0, db.Operations.Count); Equal(0L, stream.Length);
        });
        foreach (bool statusFailure in new[] { false, true })
            yield return ($"PARTSUSAGE real handler: {(statusFailure ? "ERROR after normalized insert" : "lost ACK after PROCESSED")} never duplicates its two details", async () =>
            {
                string body = Bodies["PARTSUSAGE"].Replace("\t10001\t1\r", "\t10001\t2\r") +
                    "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0\r";
                var message = Message(body); Database db = new(); Repository repo = new(db);
                PartsStore parts = new(db, statusFailure);
                PartsUsageHandler handler = new(parts, NullLogger<PartsUsageHandler>.Instance);
                NeximEventProcessor processor = new(repo, new TestLog());
                using MemoryStream first = statusFailure ? new MemoryStream() : new BrokenStream();
                await Failure(processor.ProcessAsync(first, message, true, handler.HandleAsync, Sender));
                Equal(2, parts.Details.Count); Equal(1, db.Rows.Count);
                Equal(statusFailure ? "ERROR" : "PROCESSED", db.Rows[0].Status);
                int updates = parts.StatusUpdates;
                using MemoryStream retransmittedAck = new();
                await processor.ProcessAsync(retransmittedAck, message, true, handler.HandleAsync, Sender);
                Equal(2, parts.Details.Count); Equal(updates, parts.StatusUpdates); Equal(1, db.Rows.Count);
                Check(parts.Details.All(row => row.Id == db.Rows[0].Id), "Different raw_event_id");
                Equal(body, db.Rows[0].Raw);
                Bytes(new NeximAckBuilder().Build(message, statusFailure ? 1 : 0), retransmittedAck.ToArray());
            });
    }

    private static Task Sender(Stream stream, ParsedNeximMessage message, int result, CancellationToken token) => message.EventName switch
    {
        "PARTSUSAGE" => new PartsUsageAckSender(new(), NullLogger<PartsUsageAckSender>.Instance).SendAsync(stream, message, result, token),
        "PDERROR" => new PdErrorAckSender(new(), NullLogger<PdErrorAckSender>.Instance).SendAsync(stream, message, result, token),
        "PRODSTARTED" => new ProdStartedAckSender(new(), NullLogger<ProdStartedAckSender>.Instance).SendAsync(stream, message, result, token),
        "PRODCOMPLETEDII" => new ProdCompletedIIAckSender(new(), NullLogger<ProdCompletedIIAckSender>.Instance).SendAsync(stream, message, result, token),
        "PRODSTOPPED" => new ProdStoppedAckSender(new(), NullLogger<ProdStoppedAckSender>.Instance).SendAsync(stream, message, result, token),
        "MCALARMON" => new McAlarmOnAckSender(new(), NullLogger<McAlarmOnAckSender>.Instance).SendAsync(stream, message, result, token),
        "MCALARMOFF" => new McAlarmOffAckSender(new(), NullLogger<McAlarmOffAckSender>.Instance).SendAsync(stream, message, result, token),
        _ => throw new Exception("Monitor attempted ACK")
    };
    private static ParsedNeximMessage Message(string raw) => new NeximMessageParser().Parse(raw);
    private static Task<RawEventInsertResult> Insert(Repository repository, ParsedNeximMessage message) =>
        repository.InsertRawEventOrGetExistingAsync(message.EventName, message.SeqID, message.RawMessage, DateTimeOffset.UtcNow);
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Equal<T>(T expected, T actual) => Check(Equals(expected, actual), $"Expected {expected}; got {actual}");
    private static void Bytes(byte[] expected, byte[] actual) => Check(expected.SequenceEqual(actual), "Bytes differ");
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new Exception("Expected failure"); }

    // Create actual Microsoft.Data.SqlClient exceptions; only the transport is replaced.
    private static SqlException SqlError(int number)
    {
        ConstructorInfo constructor = typeof(SqlError).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 8);
        object?[] arguments = constructor.GetParameters().Select(p => p.ParameterType == typeof(int) ? (object)(p.Position == 0 ? number : 0) :
            p.ParameterType == typeof(byte) ? (byte)0 : p.ParameterType == typeof(uint) ? (uint)0 :
            p.ParameterType == typeof(string) ? "injected SQL error" : null).ToArray();
        var error = (SqlError)constructor.Invoke(arguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(errors, [error]);
        MethodInfo factory = typeof(SqlException).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "CreateException" && method.GetParameters().Length == 2);
        return (SqlException)factory.Invoke(null, [errors, "16.0"])!;
    }
    private sealed class Row(long id, string name, string? seq, string raw, byte[]? hash)
    { public long Id = id; public string Name = name; public string? Seq = seq; public string Raw = raw; public byte[]? Hash = hash; public string Status = "PENDING"; }
    private sealed class Database
    {
        public readonly List<Row> Rows = []; public readonly System.Collections.Concurrent.ConcurrentQueue<string> Operations = new();
        public int DuplicateErrorNumber = 2601; public SqlException? LastException, InsertError;
        public object Insert(DbParameterCollection parameters)
        {
            lock (Rows)
            {
                Operations.Enqueue("INSERT");
                if (InsertError is not null) throw InsertError;
                Equal(DbType.Int32, parameters["@seq_id"].DbType);
                Equal(DbType.Binary, parameters["@raw_hash"].DbType); Equal(32, parameters["@raw_hash"].Size);
                byte[]? hash = parameters["@raw_hash"].Value as byte[];
                if (hash is not null && Rows.Any(row => row.Hash is not null && row.Hash.SequenceEqual(hash)))
                    throw LastException = SqlError(DuplicateErrorNumber);
                long id = Rows.Count + 1;
                Rows.Add(new(id, (string)parameters["@event_name"].Value!, parameters["@seq_id"].Value is DBNull ? null : Convert.ToString(parameters["@seq_id"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    (string)parameters["@raw_message"].Value!, hash));
                return id;
            }
        }
        public DbDataReader Read(DbParameterCollection parameters)
        {
            lock (Rows)
            {
                Operations.Enqueue("SELECT");
                byte[] hash = (byte[])parameters["@raw_hash"].Value!;
                DataTable table = new(); table.Columns.Add("raw_event_id", typeof(long));
                foreach (string name in new[] { "parse_status", "event_name", "seq_id", "raw_message" }) table.Columns.Add(name, typeof(string));
                foreach (var row in Rows.Where(row => row.Hash is not null && row.Hash.SequenceEqual(hash)))
                    table.Rows.Add(row.Id, row.Status, row.Name, (object?)row.Seq ?? DBNull.Value, row.Raw);
                return table.CreateDataReader();
            }
        }
    }
    private sealed class Repository(Database database) : IRawEventRepository
    {
        public int LegacyCalls;
        public async Task<RawEventInsertResult> InsertRawEventOrGetExistingAsync(string eventName, string? seqId,
            string rawMessage, DateTimeOffset receivedAt, CancellationToken cancellationToken = default)
        {
            using Connection connection = new(database);
            return await NeximRepository.InsertRawEventOrGetExistingAsync(connection, eventName, seqId, rawMessage, receivedAt, cancellationToken);
        }
        public Task<long> InsertRawEvent(string eventName, string? seqId, string rawMessage, DateTimeOffset receivedAt,
            string parseStatus, CancellationToken cancellationToken = default)
        { LegacyCalls++; throw new Exception("Deduplication bypassed"); }
    }
    private sealed class Connection(Database database) : DbConnection
    {
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "test"; public override string DataSource => "test"; public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { } public override void Close() { } public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new Command(database);
    }
    private sealed class Command(Database database) : DbCommand
    {
        private readonly SqlCommand parameters = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; } public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; } public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        public override void Cancel() { } public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new Exception("RAW must not be updated");
        public override object ExecuteScalar()
        {
            Check(CommandText.TrimStart().StartsWith("INSERT INTO dbo.nexim_raw_event"), "Expected INSERT before SELECT");
            Check(CommandText.Contains("'PENDING'"), "Initial state not PENDING");
            return database.Insert(Parameters);
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Check(CommandText.Contains("WHERE raw_hash = @raw_hash"), "Lookup not keyed by hash");
            return database.Read(Parameters);
        }
        protected override void Dispose(bool disposing) { if (disposing) parameters.Dispose(); base.Dispose(disposing); }
    }
    private sealed class TestLog : ILogger<NeximEventProcessor>
    {
        public List<(LogLevel Level, string Text)> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => Lines.Add((level, format(state, error)));
    }
    private sealed class BrokenStream : MemoryStream
    {
        public int Writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Writes++; throw new IOException("Injected disconnect"); }
    }
    private sealed class PartsStore(Database database, bool failProcessed) : IPartsUsageRepository
    {
        public List<(long Id, PartsUsageDetail Detail)> Details = [];
        public int StatusUpdates;
        public Task InsertPartsUsageDetails(long id, PartsUsageData data, CancellationToken cancellationToken = default)
        { Details.AddRange(data.Details.Select(detail => (id, detail))); return Task.CompletedTask; }
        public Task UpdateRawEventStatus(long id, string status, CancellationToken cancellationToken = default)
        {
            StatusUpdates++;
            if (failProcessed && status == "PROCESSED") throw new InvalidOperationException("Injected failure after normalized persistence");
            database.Rows.Single(row => row.Id == id).Status = status;
            return Task.CompletedTask;
        }
    }
    private sealed class LoadStore(Database database) : ILoadCompIVRepository
    {
        public int Details;
        public Task InsertLoadCompIVDetails(long id, LoadCompIVData data, CancellationToken cancellationToken = default)
        { Details += data.Details.Count; return Task.CompletedTask; }
        public Task UpdateRawEventStatus(long id, string status, CancellationToken cancellationToken = default)
        { database.Rows.Single(row => row.Id == id).Status = status; return Task.CompletedTask; }
    }
}
