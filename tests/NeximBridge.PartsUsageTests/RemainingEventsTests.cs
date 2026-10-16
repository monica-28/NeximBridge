using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NeximBridge;

internal static class RemainingEventsTests
{
    private const string Load = "LOADCOMPIV\t1006\t20260928123456\tLINE1\tNXT1\t1\t2\r" +
        "1\t5\tRES-10K\tFEEDER-005\tREEL-A\tVENDOR-A\tLOT-A\t202609\tLIGHT-A\t5000\t0\r" +
        "1\t6\tCAP-100N\tFEEDER-006\tREEL-B\tVENDOR-B\tLOT-B\t202609\tLIGHT-B\t3000\t0\r";
    private const string Change = "CHANGECOMPII\t1007\t20260928123456\tLINE1\tNXT1\t1\t1\r" +
        "1\t5\tRES-10K\tFEEDER-005\tREEL-A\tREEL-C\t4500\r";
    private const string Unload = "UNLOADCOMP\t1008\t20260928123456\tLINE1\tNXT1\t1\t1\r" +
        "1\t5\tRES-10K\tFEEDER-005\tREEL-A\t250\t120\r";
    private const string On = "MCALARMON\t1009\t20260928123456\tLINE1\tNXT1\t1\t00000001\t00000010";
    private const string Off = "MCALARMOFF\t1010\t20260928123456\tLINE1\tNXT1\t1\t00000001\t00000010";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        foreach (string body in new[] { Load, Change, Unload, On, Off })
        {
            var message = Message(body);
            bool alarm = message.EventName.StartsWith("MCALARM", StringComparison.Ordinal);
            string name = message.EventName;
            yield return ($"{name}: exact mapping, UTC, explicit NULLs, raw first and PROCESSED", async () =>
            {
                Store store = new(); using FlushStream stream = new();
                await Processor(store).ProcessAsync(stream, message, alarm);
                Equal(body, store.Raw); Equal("PENDING,insert,PROCESSED", string.Join(',', store.Calls));
                Equal(name == "LOADCOMPIV" ? 2 : 1, store.Commands.Count);
                foreach (SqlCommand command in store.Commands)
                {
                    Equal(42L, Value(command, "raw_event_id")); Equal(name, Value(command, "event_name"));
                    Equal(new DateTime(2026, 9, 28, 12, 34, 56, DateTimeKind.Utc), Value(command, "event_time_utc"));
                    Equal("LINE1", Value(command, "line_name")); Equal("NXT1", Value(command, "machine_name"));
                    Equal(1, Value(command, "module_no"));
                    Check(!command.CommandText.Contains("seq_id") && !command.CommandText.Contains("raw_message"), "raw columns in normalized SQL");
                    Dictionary<string, object> expected = Expected(name, store.Commands.IndexOf(command));
                    foreach (SqlParameter parameter in command.Parameters)
                    {
                        string column = parameter.ParameterName[1..];
                        if (column is "raw_event_id" or "event_name" or "event_time_utc" or "line_name" or "machine_name" or "module_no") continue;
                        Equal(expected.GetValueOrDefault(column, DBNull.Value), parameter.Value);
                    }
                    foreach (var (column, value) in expected) Equal(value, Value(command, column));
                    Equal(alarm ? 24 : 32, command.Parameters.Count);
                }
                if (alarm)
                {
                    Equal($"{name}_ACK\t{message.SeqID}\t0\tNXT1\t1", Content(stream));
                    Equal(1, stream.Flushes);
                }
                else Equal(0L, stream.Length);
            });
            yield return ($"{name}: Ack=false suppresses all writes and flushes", async () =>
            {
                Store store = new(); using FlushStream stream = new();
                await Processor(store).ProcessAsync(stream, message, false);
                Equal(0L, stream.Length); Equal(0, stream.Flushes);
            });
            foreach (string bad in new[] { "20260230123456", "2026-09-28T12:34:56Z", "2026092812345", "20260928123456Z" })
                yield return ($"{name}: reject Time={bad}, ERROR preserves raw", () => Reject(body.Replace("20260928123456", bad), alarm));
            foreach (int index in new[] { 1, 5 })
            foreach (string bad in new[] { "-1", "abc", "2147483648", "1.0" })
                yield return ($"{name}: reject numeric header {index}={bad}", () => Reject(Header(body, index, bad), alarm));
            yield return ($"{name}: normalized persistence failure -> ERROR, raw retained, negative ACK", async () =>
            {
                Store store = new() { FailInsert = true }; using FlushStream stream = new();
                await Failure(Processor(store).ProcessAsync(stream, message, alarm));
                Equal(body, store.Raw); Equal("PENDING,insert,ERROR", string.Join(',', store.Calls));
                Equal(0, store.Commands.Count);
                if (alarm) Equal($"{name}_ACK\t{message.SeqID}\t1\tNXT1\t1", Content(stream));
                else Equal(0L, stream.Length);
            });
            yield return ($"{name}: cancellation still marks ERROR with a fresh token", async () =>
            {
                Store store = new() { FailInsert = true, CancelInsert = true }; using FlushStream stream = new();
                await Failure(Processor(store).ProcessAsync(stream, message, false));
                Equal("ERROR", store.Calls[^1]); Check(!store.StatusTokenCancelled, "Status token cancelled");
            });
            yield return ($"{name}: failed PROCESSED update attempts ERROR and keeps raw", async () =>
            {
                Store store = new() { FailProcessed = true }; using FlushStream stream = new();
                await Failure(Processor(store).ProcessAsync(stream, message, alarm));
                Equal(body, store.Raw); Equal("PENDING,insert,PROCESSED,ERROR", string.Join(',', store.Calls));
                if (alarm) Equal($"{name}_ACK\t{message.SeqID}\t1\tNXT1\t1", Content(stream));
                else Equal(0L, stream.Length);
            });
            if (!alarm)
            {
                foreach (string bad in new[] { "0", "3", "-1", "abc" })
                    yield return ($"{name}: NumList mismatch {bad}", () => Reject(Header(body, 6, bad), false));
                yield return ($"{name}: incomplete record", () => Reject(Detail(body, fields => fields[..^1]), false));
                yield return ($"{name}: extra record field", () => Reject(Detail(body, fields => [.. fields, "extra"]), false));
                yield return ($"{name}: missing final CR", () => Reject(body[..^1], false));
                yield return ($"{name}: CRLF is not CR", () => Reject(body.Replace("\r", "\r\n"), false));
                foreach (int index in name == "LOADCOMPIV" ? new[] { 0, 1, 9, 10 } : name == "UNLOADCOMP" ? new[] { 0, 1, 5, 6 } : new[] { 0, 1, 6 })
                foreach (string bad in new[] { "-1", "NaN" })
                    yield return ($"{name}: reject detail integer {index}={bad}", () => Reject(Detail(body, fields => { fields[index] = bad; return fields; }), false));
                yield return ($"{name}: second detail fails SQL, transaction leaves no partial rows", async () =>
                {
                    string two = name == "LOADCOMPIV" ? body : Header(body, 6, "2") + body.Split('\r')[1] + "\r";
                    var templates = Commands(Message(two));
                    using FakeConnection connection = new() { FailAt = 2 };
                    await Failure(RemainingEventBatch.ExecuteAsync(connection, templates, default));
                    Equal(2, connection.Executed); Equal(0, connection.Rows.Count);
                    Check(connection.RolledBack && !connection.Committed, "Missing rollback");
                });
                yield return ($"{name}: SQL batch commits all rows in one transaction", async () =>
                {
                    using FakeConnection connection = new(); var templates = Commands(message);
                    await RemainingEventBatch.ExecuteAsync(connection, templates, default);
                    Equal(templates.Count, connection.Rows.Count); Check(connection.Committed, "Missing commit");
                    foreach (var row in connection.Rows) Equal(42L, row["@raw_event_id"]);
                });
            }
            else
            {
                foreach (int index in new[] { 6, 7 })
                foreach (string bad in new[] { "0000000", "000000001", "0000000G", "-0000001", "" })
                    yield return ($"{name}: hex code {index} rejects [{bad}]", () => Reject(Header(body, index, bad), true));
                yield return ($"{name}: accepts upper and lower case hex verbatim", () =>
                {
                    var command = Commands(Message(Header(Header(body, 6, "AaBb0010"), 7, "fF0000Ee")))[0];
                    Equal("AaBb0010", Value(command, "error_code")); Equal("fF0000Ee", Value(command, "sub_error_code"));
                    return Task.CompletedTask;
                });
                yield return ($"{name}: rejects extra field", () => Reject(body + "\textra", true));
                yield return ($"{name}: rejects missing field", () => Reject(body[..body.LastIndexOf('\t')], true));
                yield return ($"{name}: rejects CR", () => Reject(body + "\r", true));
                yield return ($"{name}: ACK send failure does not mark processed raw ERROR", async () =>
                {
                    Store store = new(); using FailingStream stream = new();
                    await Failure(Processor(store).ProcessAsync(stream, message, true));
                    Equal("PROCESSED", store.Calls[^1]); Equal(1, stream.Writes);
                });
                yield return ($"{name}: insufficient identity cannot fabricate ACK", () =>
                {
                    try { new NeximAckBuilder().Build(Message(name + "\t1009"), 1); }
                    catch (NeximAckException) { return Task.CompletedTask; }
                    throw new Exception("Fabricated ACK");
                });
            }
        }
        foreach (string name in new[] { "LOADCOMPIV", "CHANGECOMPII", "UNLOADCOMP", "RECOVERYUP", "PCBCHECKOUTVII", "UNKNOWN" })
            yield return ($"ACK: no generic fallback for {name}", () =>
            {
                try { new NeximAckBuilder().Build(Message(name + "\t1006")); }
                catch (NeximAckException) { return Task.CompletedTask; }
                throw new Exception("Generic ACK returned");
            });
        yield return ("LOADCOMPIV: reject SupplyMethod=2", () => Reject(Load.Replace("5000\t0", "5000\t2"), false));
        yield return ("LOADCOMPIV: accept splicing SupplyMethod=1", () =>
        {
            Equal(1, LoadCompIVParser.Parse(Message(Load.Replace("5000\t0", "5000\t1"))).Details[0].SupplyMethod);
            return Task.CompletedTask;
        });
        yield return ("SETEV: mixed ACK flags exact frame and policy", () =>
        {
            NeximEventSubscription[] subscriptions = [new() { Name = "PARTSUSAGE", Ack = true }, new() { Name = "LOADCOMPIV", Ack = false }, new() { Name = "MCALARMON", Ack = true }];
            var frame = new NeximSetEventBuilder().BuildSubscriptions("7", subscriptions);
            Equal("SETEV\t7\t\t3\rPARTSUSAGE\t1\rLOADCOMPIV\t0\rMCALARMON\t1\r", Encoding.UTF8.GetString(frame, 5, frame.Length - 6));
            Check(NeximAckPolicy.RequiresAck(subscriptions, "PARTSUSAGE"), "Existing ACK disabled");
            Check(!NeximAckPolicy.RequiresAck(subscriptions, "LOADCOMPIV"), "Monitor ACK enabled");
            Check(!NeximAckPolicy.RequiresAck(subscriptions, "UNKNOWN"), "Unsubscribed ACK enabled");
            return Task.CompletedTask;
        });
    }

    private static Dictionary<string, object> Expected(string name, int row)
    {
        if (name.StartsWith("MCALARM", StringComparison.Ordinal)) return new() { ["error_code"] = "00000001", ["sub_error_code"] = "00000010" };
        Dictionary<string, object> result = new() { ["stage_no"] = 1, ["slot_no"] = row == 0 ? 5 : 6,
            ["part_no"] = row == 0 ? "RES-10K" : "CAP-100N", ["feeder_id"] = row == 0 ? "FEEDER-005" : "FEEDER-006" };
        if (name == "LOADCOMPIV")
        {
            result["reel_id"] = row == 0 ? "REEL-A" : "REEL-B"; result["vendor"] = row == 0 ? "VENDOR-A" : "VENDOR-B";
            result["lot_no"] = row == 0 ? "LOT-A" : "LOT-B"; result["date_code"] = "202609";
            result["lighting_class"] = row == 0 ? "LIGHT-A" : "LIGHT-B"; result["quantity"] = row == 0 ? 5000 : 3000; result["supply_method"] = 0;
        }
        else if (name == "CHANGECOMPII") { result["old_reel_id"] = "REEL-A"; result["new_reel_id"] = "REEL-C"; result["quantity"] = 4500; }
        else { result["reel_id"] = "REEL-A"; result["quantity"] = 250; result["remaining_time"] = 120; }
        return result;
    }
    private static async Task Reject(string body, bool ack)
    {
        Store store = new(); using FlushStream stream = new();
        var error = await Failure(Processor(store).ProcessAsync(stream, Message(body), ack));
        Check(error is NeximMessageParseException, "Wrong parse failure: " + error);
        Equal(body, store.Raw); Equal("PENDING,ERROR", string.Join(',', store.Calls)); Equal(0, store.Commands.Count);
        if (!ack) Equal(0L, stream.Length);
    }
    private static RemainingEventProcessor Processor(Store store) => new(new(store, NullLogger<NeximEventProcessor>.Instance),
        new(store, NullLogger<LoadCompIVHandler>.Instance), new(store, NullLogger<ChangeCompIIHandler>.Instance),
        new(store, NullLogger<UnloadCompHandler>.Instance), new(store, NullLogger<McAlarmOnHandler>.Instance),
        new(store, NullLogger<McAlarmOffHandler>.Instance), new(new(), NullLogger<McAlarmOnAckSender>.Instance),
        new(new(), NullLogger<McAlarmOffAckSender>.Instance));
    private static ParsedNeximMessage Message(string body) => new NeximMessageParser().Parse(body);
    private static string Header(string body, int index, string value)
    { var records = body.Split('\r'); var fields = records[0].Split('\t'); fields[index] = value; records[0] = string.Join('\t', fields); return string.Join('\r', records); }
    private static string Detail(string body, Func<string[], string[]> change)
    { var records = body.Split('\r'); records[1] = string.Join('\t', change(records[1].Split('\t'))); return string.Join('\r', records); }
    private static object Value(SqlCommand command, string column) => command.Parameters["@" + column].Value;
    private static string Content(MemoryStream stream)
    {
        byte[] frame = stream.ToArray(); Equal(frame.Length - 4, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame));
        Equal((byte)2, frame[4]); Equal((byte)3, frame[^1]); return Encoding.UTF8.GetString(frame, 5, frame.Length - 6);
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Equal<T>(T expected, T actual) => Check(Equals(expected, actual), $"Expected {expected}, got {actual}");
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new Exception("Expected failure"); }
    private static List<SqlCommand> Commands(ParsedNeximMessage message)
    {
        switch (message.EventName)
        {
            case "LOADCOMPIV": var l = LoadCompIVParser.Parse(message); return l.Details.Select(d => NeximRepository.CreateLoadCompIVCommand(42, l, d)).ToList();
            case "CHANGECOMPII": var c = ChangeCompIIParser.Parse(message); return c.Details.Select(d => NeximRepository.CreateChangeCompIICommand(42, c, d)).ToList();
            case "UNLOADCOMP": var u = UnloadCompParser.Parse(message); return u.Details.Select(d => NeximRepository.CreateUnloadCompCommand(42, u, d)).ToList();
            case "MCALARMON": return [NeximRepository.CreateMcAlarmOnCommand(42, McAlarmOnParser.Parse(message))];
            case "MCALARMOFF": return [NeximRepository.CreateMcAlarmOffCommand(42, McAlarmOffParser.Parse(message))];
            default: throw new Exception("Unknown event");
        }
    }
    private sealed class Store : IRawEventRepository, ILoadCompIVRepository, IChangeCompIIRepository, IUnloadCompRepository, IMcAlarmOnRepository, IMcAlarmOffRepository
    {
        public string? Raw; public List<string> Calls = []; public List<SqlCommand> Commands = [];
        public bool FailInsert, CancelInsert, StatusTokenCancelled, FailProcessed;
        public async Task<RawEventInsertResult> InsertRawEventOrGetExistingAsync(string eventName, string? seqId,
            string rawMessage, DateTimeOffset receivedAt, CancellationToken cancellationToken = default)
        {
            long id = await InsertRawEvent(eventName, seqId, rawMessage, receivedAt, "PENDING", cancellationToken);
            return new(false, id, "PENDING", eventName, seqId, rawMessage);
        }
        public Task<long> InsertRawEvent(string name, string? seq, string raw, DateTimeOffset at, string status, CancellationToken cancellationToken = default)
        { Raw = raw; Calls.Add(status); return Task.FromResult(42L); }
        public Task UpdateRawEventStatus(long id, string status, CancellationToken cancellationToken = default)
        {
            Equal(42L, id); StatusTokenCancelled = cancellationToken.IsCancellationRequested; Calls.Add(status);
            if (FailProcessed && status == "PROCESSED") throw new InvalidOperationException("Injected status failure");
            return Task.CompletedTask;
        }
        private Task Insert(long id, Func<List<SqlCommand>> commands)
        {
            Equal(42L, id); Equal("PENDING", Calls[0]); Calls.Add("insert");
            if (CancelInsert) throw new OperationCanceledException(new CancellationToken(true));
            if (FailInsert) throw new InvalidOperationException("Injected persistence failure");
            Commands = commands(); return Task.CompletedTask;
        }
        public Task InsertLoadCompIVDetails(long id, LoadCompIVData data, CancellationToken cancellationToken = default) => Insert(id, () => data.Details.Select(d => NeximRepository.CreateLoadCompIVCommand(id, data, d)).ToList());
        public Task InsertChangeCompIIDetails(long id, ChangeCompIIData data, CancellationToken cancellationToken = default) => Insert(id, () => data.Details.Select(d => NeximRepository.CreateChangeCompIICommand(id, data, d)).ToList());
        public Task InsertUnloadCompDetails(long id, UnloadCompData data, CancellationToken cancellationToken = default) => Insert(id, () => data.Details.Select(d => NeximRepository.CreateUnloadCompCommand(id, data, d)).ToList());
        public Task InsertMcAlarmOn(long id, McAlarmOnData data, CancellationToken cancellationToken = default) => Insert(id, () => [NeximRepository.CreateMcAlarmOnCommand(id, data)]);
        public Task InsertMcAlarmOff(long id, McAlarmOffData data, CancellationToken cancellationToken = default) => Insert(id, () => [NeximRepository.CreateMcAlarmOffCommand(id, data)]);
    }
    private sealed class FlushStream : MemoryStream
    { public int Flushes; public override Task FlushAsync(CancellationToken cancellationToken) { Flushes++; return base.FlushAsync(cancellationToken); } }
    private sealed class FailingStream : MemoryStream
    { public int Writes; public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Writes++; throw new IOException("Injected disconnect"); } }

    // ADO.NET fault injection exercises the production batch executor, not a duplicate algorithm.
    private sealed class FakeConnection : DbConnection
    {
        public int FailAt, Executed; public bool Committed, RolledBack;
        public List<Dictionary<string, object>> Rows = [], Pending = [];
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "test"; public override string DataSource => "test"; public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { } public override void Close() { } public override void ChangeDatabase(string name) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTransaction(this);
        protected override DbCommand CreateDbCommand() => new FakeCommand(this);
    }
    private sealed class FakeTransaction(FakeConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;
        public override void Commit() { connection.Rows.AddRange(connection.Pending); connection.Pending.Clear(); connection.Committed = true; }
        public override void Rollback() { connection.Pending.Clear(); connection.RolledBack = true; }
    }
    private sealed class FakeCommand(FakeConnection owner) : DbCommand
    {
        private readonly SqlCommand parameters = new();
        [System.Diagnostics.CodeAnalysis.AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        public override void Cancel() { } public override void Prepare() { }
        public override int ExecuteNonQuery()
        {
            Check(DbTransaction is FakeTransaction, "Command has no transaction");
            owner.Executed++;
            if (owner.Executed == owner.FailAt) throw new InvalidOperationException("Injected SQL detail failure");
            owner.Pending.Add(parameters.Parameters.Cast<SqlParameter>().ToDictionary(p => p.ParameterName, p => p.Value)); return 1;
        }
        public override object ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) parameters.Dispose(); base.Dispose(disposing); }
    }
}
