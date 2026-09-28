using System.Buffers.Binary;
using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using NeximBridge;

internal static class ProdStoppedTests
{
    private const string Body = "PRODSTOPPED\t1005\t20260928110500\tLINE1\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1";
    private static ParsedNeximMessage Message(string body = Body) => new NeximMessageParser().Parse(body);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(bool sql)
    {
        yield return ("PRODSTOPPED exact positions: ProductMode=0, ProgramName=TESTJOB_T, PanelNo=10003", () =>
        {
            Equal(new ProdStoppedData(new DateTimeOffset(2026, 9, 28, 11, 5, 0, TimeSpan.Zero),
                "LINE1", "NXT1", 1, 1, 0, "TESTJOB_T", "10003", 5, 1), ProdStoppedParser.Parse(Message()));
            return Task.CompletedTask;
        });
        yield return ("PRODSTOPPED SQL columns, raw_event_id and NULLs", () =>
        {
            using var command = NeximRepository.CreateProdStoppedCommand(42, ProdStoppedParser.Parse(Message()));
            var expected = new Dictionary<string, object>
            {
                ["raw_event_id"] = 42L, ["event_name"] = "PRODSTOPPED",
                ["event_time_utc"] = new DateTime(2026, 9, 28, 11, 5, 0, DateTimeKind.Utc),
                ["line_name"] = "LINE1", ["machine_name"] = "NXT1", ["module_no"] = 1,
                ["lane_no"] = 1, ["product_mode"] = 0, ["program_name"] = "TESTJOB_T", ["panel_no"] = "10003",
                ["cycle_time"] = DBNull.Value, ["status"] = DBNull.Value,
                ["block_count"] = 5, ["block_skip_count"] = 1, ["bs_info_bit"] = DBNull.Value
            };
            Equal(expected.Count, command.Parameters.Count);
            foreach (var item in expected) Equal(item.Value, command.Parameters["@" + item.Key].Value);
            Check(!command.CommandText.Contains("seq_id") && !command.CommandText.Contains("raw_message"), "Unexpected raw columns");
            return Task.CompletedTask;
        });
        yield return ("PRODSTOPPED handler inserts one row then marks PROCESSED", async () =>
        {
            Store store = new(); CaptureLog<ProdStoppedHandler> log = new();
            await new ProdStoppedHandler(store, log).HandleAsync(Message(), 42);
            Equal("insert:42,status:42:PROCESSED", string.Join(',', store.Calls));
            Equal(1, store.Insertions); Equal(0, store.Data!.ProductMode);
            Equal("TESTJOB_T", store.Data.ProgramName); Equal("10003", store.Data.PanelNo);
            Check(log.Messages.Any(line => line.Contains("PRODSTOPPED raw_event_id=42: 1 registros procesados.")), "Missing log");
        });
        foreach (string bad in new[] {
            Body + "\tA200", Body + "\tA200\t45000", Body + "\r", Body + "\r1\t2\r", Body[..Body.LastIndexOf('\t')],
            Body.Replace("20260928110500", "20260230110500"), Body.Replace("\t0\t", "\tNXT1\t"),
            Body.Replace("\t0\t", "\t-1\t"), Body.Replace("\t0\t", "\t2147483648\t"),
            Body.Replace("TESTJOB_T", ""), Body.Replace("1005", "0"),
            Body.Replace("\t5\t1", "\tbad\t1"), Body.Replace("\t5\t1", "\t5\t-1"),
            Body.Replace("\t5\t1", "\t2147483648\t1"), Body.Replace("\t5\t1", "\t5\t1.5") })
            yield return ("PRODSTOPPED malformed message: ERROR and no normalized insert", async () =>
            {
                Store store = new(); var message = Message(bad); CaptureLog<ProdStoppedHandler> log = new();
                var error = await Failure(new ProdStoppedHandler(store, log).HandleAsync(message, 42));
                Check(error is NeximMessageParseException, "Wrong exception");
                Equal("status:42:ERROR", string.Join(',', store.Calls)); Equal(0, store.Insertions);
                Equal(bad, message.RawMessage); Check(log.Errors == 1, "Error not logged");
            });
        yield return ("PRODSTOPPED persistence failure marks ERROR and preserves original error", async () =>
        {
            Exception expected = new IOException("SQL failure"); Store store = new() { InsertError = expected };
            var actual = await Failure(new ProdStoppedHandler(store, new CaptureLog<ProdStoppedHandler>()).HandleAsync(Message(), 42));
            Check(ReferenceEquals(expected, actual), "Original error lost");
            Equal("insert:42,status:42:ERROR", string.Join(',', store.Calls));
        });
        yield return ("PRODSTOPPED status failure attempts ERROR without losing original failure", async () =>
        {
            Store store = new() { StatusError = true }; CaptureLog<ProdStoppedHandler> log = new();
            var error = await Failure(new ProdStoppedHandler(store, log).HandleAsync(Message(), 42));
            Equal("PROCESSED failed", error.Message); Equal(2, log.Errors);
            Equal("insert:42,status:42:PROCESSED,status:42:ERROR", string.Join(',', store.Calls));
        });
        foreach (int result in new[] { 0, 1 })
            yield return ($"PRODSTOPPED_ACK Result={result}: exact eleven fields, framing and FlushAsync", async () =>
            {
                using FlushStream stream = new(); CaptureLog<ProdStoppedAckSender> log = new();
                await new ProdStoppedAckSender(new(), log).SendAsync(stream, Message(), result);
                string expected = $"PRODSTOPPED_ACK\t1005\t{result}\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1";
                byte[] frame = stream.ToArray();
                Equal(Encoding.UTF8.GetByteCount(expected) + 2, BinaryPrimitives.ReadInt32BigEndian(frame));
                Equal((byte)2, frame[4]); Equal((byte)3, frame[^1]);
                Equal(expected, Encoding.UTF8.GetString(frame[5..^1]));
                Check(stream.Flushed && stream.CanWrite, "Stream was not flushed or was closed");
                Check(log.Messages.Any(line => line.StartsWith($"TX PRODSTOPPED_ACK SeqID=1005 Result={result} Machine=NXT1")), "Missing TX log");
            });
        yield return ("PRODSTOPPED error ACK uses available identity when Time is invalid", () =>
        {
            var message = Message(Body.Replace("20260928110500", "invalid"));
            Equal("PRODSTOPPED_ACK\t1005\t1\tNXT1\t1\t1\t0\tTESTJOB_T\t10003\t5\t1",
                Encoding.UTF8.GetString(new NeximAckBuilder().Build(message, 1)[5..^1]));
            return Task.CompletedTask;
        });
        if (sql) yield return ("PRODSTOPPED SQL Server: one row, exact mappings and raw status (ROLLBACK)", SqlTest);
    }

    private static async Task SqlTest()
    {
        var config = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath("appsettings.json")).Build();
        await using SqlConnection connection = new(config["Nexim:SQL:ConnectionString"]);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using SqlCommand raw = new("""
                INSERT INTO dbo.nexim_raw_event (event_name,seq_id,raw_message,received_at,parse_status)
                OUTPUT INSERTED.raw_event_id VALUES ('PRODSTOPPED',1005,@raw,SYSUTCDATETIME(),'PENDING');
                """, connection, transaction);
            raw.Parameters.Add("@raw", SqlDbType.NVarChar, -1).Value = Body;
            long id = Convert.ToInt64(await raw.ExecuteScalarAsync());
            using var insert = NeximRepository.CreateProdStoppedCommand(id, ProdStoppedParser.Parse(Message()));
            insert.Connection = connection; insert.Transaction = transaction;
            await insert.ExecuteNonQueryAsync();
            await using (SqlCommand query = new("SELECT * FROM dbo.nexim_production_event WHERE raw_event_id=@id", connection, transaction))
            {
                query.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await using var rows = await query.ExecuteReaderAsync();
                Check(await rows.ReadAsync(), "Missing production row");
                foreach (SqlParameter parameter in insert.Parameters) Equal(parameter.Value, rows[parameter.ParameterName[1..]]);
                Check(!await rows.ReadAsync(), "More than one production row");
            }
            foreach (string status in new[] { "PROCESSED", "ERROR" })
            {
                using var update = NeximRepository.CreateRawEventStatusCommand(id, status);
                update.Connection = connection; update.Transaction = transaction;
                Equal(id, Convert.ToInt64(await update.ExecuteScalarAsync()));
                await using SqlCommand verify = new("SELECT raw_message,parse_status FROM dbo.nexim_raw_event WHERE raw_event_id=@id", connection, transaction);
                verify.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await using var reader = await verify.ExecuteReaderAsync();
                Check(await reader.ReadAsync(), "Missing raw"); Equal(Body, reader.GetString(0)); Equal(status, reader.GetString(1));
            }
        }
        finally { await transaction.RollbackAsync(); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; got {actual}");
    private static async Task<Exception> Failure(Task task)
    { try { await task; } catch (Exception error) { return error; } throw new Exception("Expected failure"); }
    private sealed class Store : IProdStoppedRepository
    {
        public List<string> Calls { get; } = [];
        public int Insertions { get; private set; }
        public ProdStoppedData? Data { get; private set; }
        public Exception? InsertError { get; init; }
        public bool StatusError { get; init; }
        public Task InsertProdStopped(long id, ProdStoppedData data, CancellationToken token = default)
        {
            Calls.Add($"insert:{id}"); if (InsertError is not null) throw InsertError;
            Insertions++; Data = data; return Task.CompletedTask;
        }
        public Task UpdateRawEventStatus(long id, string status, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Calls.Add($"status:{id}:{status}"); if (StatusError) throw new IOException(status + " failed"); return Task.CompletedTask; }
    }
    private sealed class CaptureLog<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public int Errors { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { Messages.Add(formatter(state, error)); if (level == LogLevel.Error) Errors++; }
    }
    private sealed class FlushStream : MemoryStream
    {
        public bool Flushed { get; private set; }
        public override Task FlushAsync(CancellationToken token) { Flushed = true; return base.FlushAsync(token); }
    }
}
