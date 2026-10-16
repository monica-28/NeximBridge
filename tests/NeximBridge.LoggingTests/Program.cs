using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeximBridge;
using Serilog;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
    passed++;
}
string root = Path.Combine(Path.GetTempPath(), "NeximLoggingTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
IConfiguration Config(string path, long size = 20971520, int retained = 30) => new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?> {
        ["NeximLogging:FilePath"] = path,
        ["NeximLogging:FileSizeLimitBytes"] = size.ToString(),
        ["NeximLogging:RetainedFileCountLimit"] = retained.ToString(),
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft"] = "Warning"
    }).Build();
string previous = Environment.CurrentDirectory;
try
{
    Environment.CurrentDirectory = root;
    Check(NeximLogging.ResolvePath("logs/test-.log") == Path.Combine(AppContext.BaseDirectory, "logs", "test-.log"), "relative path uses application base, not CWD");
    Environment.CurrentDirectory = previous;
    var config = Config(Path.Combine(root, "normal", "test-.log"));
    using (var sink = NeximLogging.CreateFileLogger(config))
    using (var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => {
        logging.ClearProviders();
        logging.AddConfiguration(config.GetSection("Logging"));
        logging.AddSimpleConsole();
        logging.AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(sink, dispose: false));
    }).Build())
    {
        await host.StartAsync(); // No Nexim services registered; no TCP or SQL.
        Check(true, "host starts with logging configuration");
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NeximBridge.LoggingTest");
        logger.LogInformation("Information marker SeqID={SeqID}", 3001);
        logger.LogDebug("Debug payload hidden");
        try { throw new InvalidOperationException("error marker"); }
        catch (Exception error) { logger.LogError(error, "Error marker"); }
        await host.StopAsync();
        logger.LogInformation("Final flush marker");
    }
    string file = Directory.GetFiles(Path.Combine(root, "normal"), "*.log").Single();
    string content = File.ReadAllText(file);
    Check(content.Contains("Information marker SeqID=3001"), "Information persisted");
    Check(content.Contains("NeximBridge.LoggingTest") && content.Contains("[INF]"), "category and level persisted");
    Check(content.Contains("InvalidOperationException: error marker") && content.Contains("Program.cs"), "Error exception and stack trace persisted");
    Check(!content.Contains("Debug payload hidden"), "Information filters Debug");
    Check(content.Contains("Final flush marker"), "shutdown flushes final event");
    using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Check(true, "shutdown releases file handle");
    using (var sink = NeximLogging.CreateFileLogger(Config(Path.Combine(root, "rolling", "test-.log"), 200, 3)))
        for (int i = 0; i < 20; i++) sink.Information("Rolling {Index} {Padding}", i, new string('x', 250));
    Check(Directory.GetFiles(Path.Combine(root, "rolling")).Length == 3, "size rotation retains only configured file count");
    string blocker = Path.Combine(root, "not-a-directory");
    File.WriteAllText(blocker, "block");
    using (var sink = NeximLogging.CreateFileLogger(Config(Path.Combine(blocker, "test-.log"))))
    using (var factory = LoggerFactory.Create(logging => logging.AddSimpleConsole().AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(sink, dispose: false))))
        factory.CreateLogger("Fallback").LogInformation("Console fallback marker");
    Check(true, "unwritable file does not prevent logging or processing");
    Console.WriteLine($"{passed}/{passed} passed. Temporary logs: {root}");
}
finally { Environment.CurrentDirectory = previous; }
