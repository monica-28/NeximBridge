using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using NeximBridge;

internal static class PdErrorTests
{
    private const string Header = "PDERROR\t1002\t20260928123456\tLINE1\tNXT1\t1\t1\tTESTJOB_T";
    private const string Details = "1\t5\tRES-10K\tRES-10K-AVL\t2\t3\t4\t5\t6\tR101\t80000001\t00000002\r" +
        "1\t6\tCAP-100N\tCAP-100N-AVL\t7\t8\t1\t10\t11\tC201\tFFFFFFFF\t00000000\r";
    private const string Body = Header + "\t2\r" + Details;
    private static readonly PdErrorDetail First = new(1, 5, "RES-10K", "RES-10K-AVL", 2, 3, 4, 5, 6, "R101", "80000001", "00000002");
    private static readonly PdErrorDetail Second = new(1, 6, "CAP-100N", "CAP-100N-AVL", 7, 8, 1, 10, 11, "C201", "FFFFFFFF", "00000000");
    private static ParsedNeximMessage Message(string body = Body) => new NeximMessageParser().Parse(body);

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(bool sql)
    {
        foreach (string body in new[] { Body })
        {
            yield return ("PDERROR parse: two complete records, UTC, no PanelNo", () =>
            {
                PdErrorData data = PdErrorParser.Parse(Message(body));
                Equal(2, data.Details.Count); Equal(First, data.Details[0]); Equal(Second, data.Details[1]);
                Equal("LINE1", data.LineName); Equal("NXT1", data.MachineName);
                Equal(1, data.ModuleNo); Equal(1, data.LaneNo); Equal("TESTJOB_T", data.ProgramName);
                Equal(new DateTimeOffset(2026, 9, 28, 12, 34, 56, TimeSpan.Zero), data.EventTimeUtc);
                return Task.CompletedTask;
            });
            yield return ("PDERROR SQL: all 24 mapped columns and absent fields NULL", () =>
            {
                var data = PdErrorParser.Parse(Message(body));
                foreach (var detail in data.Details)
                {
                    using var command = NeximRepository.CreatePdErrorCommand(42, data, detail);
                    var expected = new Dictionary<string, object>
                    {
                        ["raw_event_id"] = 42L, ["event_name"] = "PDERROR",
                        ["event_time_utc"] = new DateTime(2026, 9, 28, 12, 34, 56, DateTimeKind.Utc),
                        ["line_name"] = "LINE1", ["machine_name"] = "NXT1", ["module_no"] = 1,
                        ["lane_no"] = 1, ["program_name"] = "TESTJOB_T", ["panel_no"] = DBNull.Value,
                        ["stage_no"] = detail.StageNo, ["slot_no"] = detail.SlotNo, ["part_no"] = detail.PartName,
                        ["avl_name"] = detail.AvlName, ["head_no"] = detail.HeadNo, ["holder_no"] = detail.HolderNo,
                        ["nozzle_no"] = detail.NozzleNo, ["nozzle_pit_no"] = detail.NozzlePitNo,
                        ["board_no"] = detail.BoardNo, ["reference"] = detail.Reference,
                        ["error_code"] = detail.ErrorCode, ["sub_error_code"] = detail.SubErrorCode,
                        ["reel_id"] = DBNull.Value, ["feeder_id"] = DBNull.Value, ["description"] = DBNull.Value
                    };
                    Equal(expected.Count, command.Parameters.Count);
                    foreach (var item in expected) Equal(item.Value, command.Parameters["@" + item.Key].Value);
                    foreach (string forbidden in new[] { "seq_id", "raw_message", "received_at", "event_at" })
                        Check(!command.CommandText.Contains(forbidden), "Forbidden incident column");
                }
                return Task.CompletedTask;
            });
        }
        yield return ("PDERROR handler: same raw ID, two details, PROCESSED and log", async () =>
        {
            Store store = new(); Log<PdErrorHandler> log = new();
            await new PdErrorHandler(store, log).HandleAsync(Message(), 42);
            Equal(2, store.Rows.Count); Check(store.Rows.All(row => row.Id == 42), "Wrong raw ID");
            Equal("insert:42,status:42:PROCESSED", string.Join(',', store.Calls));
            Check(log.Messages.Contains("PDERROR raw_event_id=42: 2 registros procesados."), "Missing processing log");
        });
        foreach (string invalid in new[] {
            Header + "\t10001\t2\r" + Details,
            Body.Replace("\t2\r", "\t3\r"), Body.Replace("\t2\r", "\tNumList\r"),
            Body[..^1], Body + "\r", Body.Replace("\t7\t8\t", "\t7\t"),
            Body.Replace("\t7\t8\t", "\t7\t8\t9\t"), Body.Replace("20260928123456", "20260230123456"),
            Body.Replace("\t7\t8\t", "\tNXT1\t8\t"), Body.Replace("\t7\t8\t", "\t-1\t8\t"),
            Body.Replace("FFFFFFFF", "XYZ00000"), Body.Replace("80000001", "123") })
        {
            yield return ("PDERROR malformed input: ERROR, original raw intact, no details", async () =>
            {
                Store store = new(); Log<PdErrorHandler> log = new(); var message = Message(invalid);
                Exception error = await Failure(new PdErrorHandler(store, log).HandleAsync(message, 42));
                Check(error is NeximMessageParseException, "Wrong parsing exception");
                Equal("status:42:ERROR", string.Join(',', store.Calls)); Equal(0, store.Rows.Count);
                Equal(invalid, message.RawMessage); Check(log.Errors > 0, "Missing error log");
            });
        }
        yield return ("PDERROR persistence error is preserved and status becomes ERROR", async () =>
        {
            Exception expected = new IOException("SQL failure"); Store store = new() { Failure = expected };
            var error = await Failure(new PdErrorHandler(store, new Log<PdErrorHandler>()).HandleAsync(Message(), 42));
            Check(ReferenceEquals(expected, error), "Original error lost");
            Equal("insert:42,status:42:ERROR", string.Join(',', store.Calls));
        });
        yield return ("PDERROR status failure logs both errors and preserves original", async () =>
        {
            Store store = new() { FailStatus = true }; Log<PdErrorHandler> log = new();
            var error = await Failure(new PdErrorHandler(store, log).HandleAsync(Message(), 42));
            Equal("PROCESSED failed", error.Message); Equal(2, log.Errors);
            Equal("insert:42,status:42:PROCESSED,status:42:ERROR", string.Join(',', store.Calls));
        });
        yield return ("PDERROR cancelled processing still marks ERROR", async () =>
        {
            using CancellationTokenSource stop = new(); stop.Cancel();
            Store store = new() { Failure = new OperationCanceledException(stop.Token) };
            var error = await Failure(new PdErrorHandler(store, new Log<PdErrorHandler>()).HandleAsync(Message(), 42, stop.Token));
            Check(error is OperationCanceledException, "Cancellation lost");
            Equal("insert:42,status:42:ERROR", string.Join(',', store.Calls));
        });
        yield return ("PDERROR UTC independent of culture; zero NumList has zero details", () =>
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var data = PdErrorParser.Parse(Message(Header + "\t0\r"));
                Equal(0, data.Details.Count); Equal(TimeSpan.Zero, data.EventTimeUtc.Offset);
            }
            finally { CultureInfo.CurrentCulture = old; }
            return Task.CompletedTask;
        });
        foreach (int result in new[] { 0, 1 })
        {
            yield return ($"PDERROR_ACK Result={result}: seven official fields, existing stream, flush and logs", async () =>
            {
                Log<PdErrorAckSender> log = new(); using FlushStream stream = new();
                await new PdErrorAckSender(new(), log).SendAsync(stream, Message(), result);
                byte[] frame = stream.ToArray();
                string expected = $"PDERROR_ACK\t1002\t{result}\tNXT1\t1\t1\tTESTJOB_T";
                Equal(Encoding.UTF8.GetByteCount(expected) + 2, BinaryPrimitives.ReadInt32BigEndian(frame));
                Equal((byte)2, frame[4]); Equal((byte)3, frame[^1]);
                Equal(expected, Encoding.UTF8.GetString(frame[5..^1]));
                Check(stream.Flushed && stream.CanWrite, "No flush or stream closed");
                Check(log.Messages.Any(line => line.StartsWith($"TX PDERROR_ACK SeqID=1002 Result={result}")), "Missing TX log");
            });
        }
        yield return ("PDERROR negative ACK echoes header even when NumList is invalid", () =>
        {
            var message = Message(Body.Replace("\t2\r", "\twrong\r"));
            Equal("PDERROR_ACK\t1002\t1\tNXT1\t1\t1\tTESTJOB_T", Encoding.UTF8.GetString(new NeximAckBuilder().Build(message, 1)[5..^1]));
            return Task.CompletedTask;
        });
        if (sql) yield return ("PDERROR SQL Server: two real incident rows and raw status (ROLLBACK)", SqlTest);
    }

    private static async Task SqlTest()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath("appsettings.json")).Build();
        await using SqlConnection connection = new(config["Nexim:SQL:ConnectionString"]);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            foreach (string body in new[] { Body })
            {
                await using SqlCommand raw = new("""
                    INSERT INTO dbo.nexim_raw_event (event_name,seq_id,raw_message,received_at,parse_status)
                    OUTPUT INSERTED.raw_event_id VALUES ('PDERROR',1002,@raw,SYSUTCDATETIME(),'PENDING');
                    """, connection, transaction);
                raw.Parameters.Add("@raw", SqlDbType.NVarChar, -1).Value = body;
                long id = Convert.ToInt64(await raw.ExecuteScalarAsync());
                var data = PdErrorParser.Parse(Message(body));
                foreach (var detail in data.Details)
                {
                    await using var insert = NeximRepository.CreatePdErrorCommand(id, data, detail);
                    insert.Connection = connection; insert.Transaction = transaction;
                    await insert.ExecuteNonQueryAsync();
                }
                foreach (string status in new[] { "PROCESSED", "ERROR" })
                {
                    await using var update = NeximRepository.CreateRawEventStatusCommand(id, status);
                    update.Connection = connection; update.Transaction = transaction;
                    Equal(id, Convert.ToInt64(await update.ExecuteScalarAsync()));
                    await using SqlCommand verify = new("SELECT raw_message,parse_status FROM dbo.nexim_raw_event WHERE raw_event_id=@id", connection, transaction);
                    verify.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    await using var reader = await verify.ExecuteReaderAsync();
                    Check(await reader.ReadAsync(), "Missing raw"); Equal(body, reader.GetString(0)); Equal(status, reader.GetString(1));
                }
                await using SqlCommand query = new("SELECT * FROM dbo.nexim_incident_event WHERE raw_event_id=@id ORDER BY slot_no", connection, transaction);
                query.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await using var rows = await query.ExecuteReaderAsync(); int count = 0;
                while (await rows.ReadAsync())
                {
                    using var expected = NeximRepository.CreatePdErrorCommand(id, data, data.Details[count++]);
                    foreach (SqlParameter parameter in expected.Parameters)
                        Equal(parameter.Value, rows[parameter.ParameterName[1..]]);
                }
                Equal(2, count);
            }
        }
        finally { await transaction.RollbackAsync(); }
    }

    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<Exception> Failure(Task task)
    {
        try { await task; } catch (Exception error) { return error; }
        throw new Exception("Expected failure");
    }
    private sealed class Store : IPdErrorRepository
    {
        public List<(long Id, PdErrorDetail Detail)> Rows { get; } = [];
        public List<string> Calls { get; } = [];
        public Exception? Failure { get; init; }
        public bool FailStatus { get; init; }
        public Task InsertPdErrorDetails(long id, PdErrorData data, CancellationToken token = default)
        {
            Calls.Add($"insert:{id}"); if (Failure is not null) throw Failure;
            Rows.AddRange(data.Details.Select(detail => (id, detail))); return Task.CompletedTask;
        }
        public Task UpdateRawEventStatus(long id, string status, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Calls.Add($"status:{id}:{status}");
            if (FailStatus) throw new IOException(status + " failed"); return Task.CompletedTask;
        }
    }
    private sealed class Log<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public int Errors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        void ILogger.Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, error)); if (level == LogLevel.Error) Errors++; }
    }
    private sealed class FlushStream : MemoryStream
    {
        public bool Flushed { get; private set; }
        public override Task FlushAsync(CancellationToken token) { Flushed = true; return base.FlushAsync(token); }
    }
}
