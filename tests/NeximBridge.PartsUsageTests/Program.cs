global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using NeximBridge;
using System.Data;
using System.Globalization;

const string body = "PARTSUSAGE\t1001\t20260928123456\tLINE1\tNXT1\t1\t1\tTESTJOB_T\t10001\t2\r" +
    "1\t5\tRES-10K\tRES-10K-AVL\t100\t102\t2\t1\t1\t0\t0\t0\r" +
    "1\t6\tCAP-100N\tCAP-100N-AVL\t50\t50\t0\t0\t0\t0\t0\t0\r";
List<(string Name, Func<Task> Run)> tests = [];
tests.AddRange(AckTests.Cases(body));
tests.AddRange(RemainingEventsTests.Cases());
tests.AddRange(PdErrorTests.Cases(args.Contains("--sql")));
tests.AddRange(ProdStartedTests.Cases(args.Contains("--sql")));
tests.AddRange(ProdCompletedIITests.Cases(args.Contains("--sql")));
tests.AddRange(ProdStoppedTests.Cases(args.Contains("--sql")));
void Test(string name, Func<Task> run) => tests.Add((name, run));
ParsedNeximMessage Message(string? text = null) => new NeximMessageParser().Parse(text ?? body);

Test("Official header, UTC and both complete details; NXT1 is MachineName", () =>
{
    PartsUsageData data = PartsUsageParser.Parse(Message());
    Equal(new DateTimeOffset(2026, 9, 28, 12, 34, 56, TimeSpan.Zero), data.EventTimeUtc);
    Equal("LINE1", data.LineName); Equal("NXT1", data.MachineName);
    Equal(1, data.ModuleNo); Equal(1, data.LaneNo);
    Equal("TESTJOB_T", data.ProgramName); Equal("10001", data.PanelNo);
    Equal(2, data.Details.Count);
    Equal(new PartsUsageDetail(1, 5, "RES-10K", "RES-10K-AVL", 100, 102, 2, 1, 1, 0, 0, 0), data.Details[0]);
    Equal(new PartsUsageDetail(1, 6, "CAP-100N", "CAP-100N-AVL", 50, 50, 0, 0, 0, 0, 0, 0), data.Details[1]);
    return Task.CompletedTask;
});
Test("Handler persists two rows with the same raw_event_id before PROCESSED", async () =>
{
    Store store = new();
    await new PartsUsageHandler(store, new TestLogger()).HandleAsync(Message(), 42);
    Equal(2, store.Rows.Count);
    Check(store.Rows.All(row => row.RawId == 42), "Different raw_event_id");
    Equal("insert:42,status:42:PROCESSED", string.Join(',', store.Calls));
});
Test("SQL command uses only schema columns, every counter and explicit NULLs", () =>
{
    PartsUsageData data = PartsUsageParser.Parse(Message());
    foreach (PartsUsageDetail detail in data.Details)
    {
        using SqlCommand command = NeximRepository.CreatePartsUsageCommand(42, data, detail);
        var p = command.Parameters;
        Equal(42L, p["@raw_event_id"].Value); Equal("PARTSUSAGE", p["@event_name"].Value);
        Equal("NXT1", p["@machine_name"].Value); Equal("LINE1", p["@line_name"].Value);
        Equal(1, p["@module_no"].Value); Equal(1, p["@lane_no"].Value);
        Equal("TESTJOB_T", p["@program_name"].Value); Equal("10001", p["@panel_no"].Value);
        Equal(data.EventTimeUtc.UtcDateTime, p["@event_time_utc"].Value);
        Equal(SqlDbType.DateTime2, p["@event_time_utc"].SqlDbType);
        Equal(detail.StageNo, p["@stage_no"].Value); Equal(detail.SlotNo, p["@slot_no"].Value);
        Equal(detail.PartName, p["@part_no"].Value); Equal(detail.AvlName, p["@avl_name"].Value);
        Equal(detail.PartsUsage, p["@parts_usage"].Value); Equal(detail.PickupCount, p["@pickup_count"].Value);
        Equal(detail.ErrorParts, p["@error_parts"].Value); Equal(detail.ErrorReject, p["@error_reject"].Value);
        Equal(detail.RejectParts, p["@reject_parts"].Value); Equal(detail.DislodgedParts, p["@dislodged_parts"].Value);
        Equal(detail.RescanCount, p["@rescan_count"].Value); Equal(detail.NoPickup, p["@no_pickup"].Value);
        foreach (string name in new[] { "quantity", "feeder_id", "reel_id", "old_reel_id", "new_reel_id", "vendor", "lot_no", "date_code" })
            Equal(DBNull.Value, p["@" + name].Value);
        foreach (string forbidden in new[] { "seq_id", "part_number", "detail_message", "raw_message", "received_at" })
            Check(!command.CommandText.Contains(forbidden) && !p.Contains("@" + forbidden), "Forbidden column " + forbidden);
    }
    return Task.CompletedTask;
});
foreach (var mutation in new (string Name, string Text)[] {
    ("NumList mismatch", body.Replace("\t2\r", "\t3\r")),
    ("NumList literal", body.Replace("\t2\r", "\tNumList\r")),
    ("Missing final CR", body[..^1]),
    ("Extra record", body + "1\t2\r"),
    ("Missing detail field", body.Replace("\t102\t2", "\t2")),
    ("Extra detail field", body.Replace("\t102\t2", "\t102\t0\t2")),
    ("Invalid Time", body.Replace("20260928123456", "20260230123456")),
    ("Machine used as count must fail", body.Replace("\t100\t102", "\tNXT1\t102")),
    ("Negative count", body.Replace("\t100\t102", "\t-1\t102")),
    ("Overflow", body.Replace("\t100\t102", "\t2147483648\t102")),
    ("Bad second record", body.Replace("\t50\t50", "\tx\t50")) })
{
    Test(mutation.Name + ": ERROR and no normalized writes", async () =>
    {
        Store store = new(); TestLogger log = new(); ParsedNeximMessage message = Message(mutation.Text);
        Exception error = await Failure(new PartsUsageHandler(store, log).HandleAsync(message, 42));
        Check(error is NeximMessageParseException, "Wrong exception");
        Equal(0, store.Rows.Count); Equal("status:42:ERROR", string.Join(',', store.Calls));
        Equal(mutation.Text, message.RawMessage); Check(log.Errors > 0, "Error not logged");
    });
}
Test("Persistence failure marks ERROR, retains original exception and logs", async () =>
{
    Exception expected = new InvalidOperationException("SQL failure");
    Store store = new() { InsertFailure = expected }; TestLogger log = new();
    Exception actual = await Failure(new PartsUsageHandler(store, log).HandleAsync(Message(), 42));
    Check(ReferenceEquals(expected, actual), "Lost original failure");
    Equal("insert:42,status:42:ERROR", string.Join(',', store.Calls)); Check(log.Errors > 0, "Missing log");
});
Test("Status update failure attempts ERROR and logs status failure without hiding original", async () =>
{
    Store store = new() { StatusFailure = true }; TestLogger log = new();
    Exception error = await Failure(new PartsUsageHandler(store, log).HandleAsync(Message(), 42));
    Equal("PROCESSED status failure", error.Message);
    Equal("insert:42,status:42:PROCESSED,status:42:ERROR", string.Join(',', store.Calls));
    Equal(2, log.Errors);
});
Test("Cancellation still uses a fresh token to record ERROR", async () =>
{
    using CancellationTokenSource stop = new(); stop.Cancel();
    Store store = new() { InsertFailure = new OperationCanceledException(stop.Token) };
    Exception error = await Failure(new PartsUsageHandler(store, new TestLogger()).HandleAsync(Message(), 42, stop.Token));
    Check(error is OperationCanceledException, "Cancellation lost");
    Equal("insert:42,status:42:ERROR", string.Join(',', store.Calls));
});
Test("UTC and integers are independent of current culture", () =>
{
    CultureInfo previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        var data = PartsUsageParser.Parse(Message());
        Equal(TimeSpan.Zero, data.EventTimeUtc.Offset); Equal(100, data.Details[0].PartsUsage);
    }
    finally { CultureInfo.CurrentCulture = previous; }
    return Task.CompletedTask;
});
Test("Zero NumList creates no phantom detail", async () =>
{
    Store store = new();
    await new PartsUsageHandler(store, new TestLogger()).HandleAsync(Message(body.Split('\r')[0][..^1] + "0\r"), 42);
    Equal(0, store.Rows.Count); Equal("insert:42,status:42:PROCESSED", string.Join(',', store.Calls));
});
Test("Status UPDATE changes only parse_status by raw_event_id", () =>
{
    using SqlCommand command = NeximRepository.CreateRawEventStatusCommand(42, "ERROR");
    Equal(42L, command.Parameters["@raw_event_id"].Value);
    Equal("ERROR", command.Parameters["@status"].Value);
    Check(!command.CommandText.Contains("raw_message"), "Raw message must not be changed");
    return Task.CompletedTask;
});

if (args.Contains("--sql"))
    Test("SQL Server: actual columns, two rows, NULLs and status updates (ROLLBACK)", async () =>
    {
        string settingsPath = Path.GetFullPath("appsettings.json");
        IConfiguration config = new ConfigurationBuilder().AddJsonFile(settingsPath).Build();
        await using SqlConnection connection = new(config["Nexim:SQL:ConnectionString"]);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            // All test data, including raw and normalized rows, is rolled back.
            await using SqlCommand raw = new("""
                INSERT INTO dbo.nexim_raw_event (event_name, seq_id, raw_message, received_at, parse_status)
                OUTPUT INSERTED.raw_event_id VALUES ('PARTSUSAGE', 1001, @raw, SYSUTCDATETIME(), 'PENDING');
                """, connection, transaction);
            raw.Parameters.Add("@raw", SqlDbType.NVarChar, -1).Value = body;
            long id = Convert.ToInt64(await raw.ExecuteScalarAsync());
            PartsUsageData data = PartsUsageParser.Parse(Message());
            foreach (var detail in data.Details)
            {
                await using SqlCommand command = NeximRepository.CreatePartsUsageCommand(id, data, detail);
                command.Connection = connection; command.Transaction = transaction;
                await command.ExecuteNonQueryAsync();
            }
            foreach (string status in new[] { "PROCESSED", "ERROR" })
            {
                await using SqlCommand update = NeximRepository.CreateRawEventStatusCommand(id, status);
                update.Connection = connection; update.Transaction = transaction;
                Equal(id, Convert.ToInt64(await update.ExecuteScalarAsync()));
                await using SqlCommand verify = new("SELECT parse_status, raw_message FROM dbo.nexim_raw_event WHERE raw_event_id=@id", connection, transaction);
                verify.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                await using var reader = await verify.ExecuteReaderAsync();
                Check(await reader.ReadAsync(), "Missing raw"); Equal(status, reader.GetString(0)); Equal(body, reader.GetString(1));
            }
            await using SqlCommand rows = new("SELECT * FROM dbo.nexim_component_event WHERE raw_event_id=@id ORDER BY slot_no", connection, transaction);
            rows.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            await using var result = await rows.ExecuteReaderAsync();
            int count = 0;
            while (await result.ReadAsync())
            {
                var detail = data.Details[count++];
                using var expected = NeximRepository.CreatePartsUsageCommand(id, data, detail);
                foreach (SqlParameter parameter in expected.Parameters)
                    Equal(parameter.Value, result[parameter.ParameterName[1..]]);
            }
            Equal(2, count);
        }
        finally { await transaction.RollbackAsync(); }
    });

int failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected [{expected}], actual [{actual}]");
static async Task<Exception> Failure(Task task)
{
    try { await task; } catch (Exception exception) { return exception; }
    throw new Exception("Expected failure");
}
sealed class Store : IPartsUsageRepository
{
    public List<(long RawId, PartsUsageDetail Detail)> Rows { get; } = [];
    public List<string> Calls { get; } = [];
    public Exception? InsertFailure { get; init; }
    public bool StatusFailure { get; init; }
    public Task InsertPartsUsageDetails(long id, PartsUsageData data, CancellationToken token = default)
    {
        Calls.Add($"insert:{id}");
        if (InsertFailure is not null) throw InsertFailure;
        Rows.AddRange(data.Details.Select(detail => (id, detail)));
        return Task.CompletedTask;
    }
    public Task UpdateRawEventStatus(long id, string status, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Calls.Add($"status:{id}:{status}");
        if (StatusFailure) throw new IOException(status + " status failure");
        return Task.CompletedTask;
    }
}
sealed class TestLogger : ILogger<PartsUsageHandler>
{
    public int Errors { get; private set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    { if (level == LogLevel.Error) Errors++; }
}
