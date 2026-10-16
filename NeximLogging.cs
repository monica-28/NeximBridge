using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;

namespace NeximBridge;

public static class NeximLogging
{
    private static long lastDiagnostic;

    public static string ResolvePath(string path) => Path.GetFullPath(path, AppContext.BaseDirectory);

    public static Logger CreateFileLogger(IConfiguration configuration)
    {
        // Never forward sink diagnostics (which can include event contents) to production logs.
        Serilog.Debugging.SelfLog.Enable(_ => ReportFileFailure());
        try
        {
            string path = ResolvePath(configuration["NeximLogging:FilePath"] ?? "logs/neximbridge-.log");
            int retained = configuration.GetValue("NeximLogging:RetainedFileCountLimit", 30);
            long size = configuration.GetValue("NeximLogging:FileSizeLimitBytes", 20971520L);
            if (retained < 1 || size < 1)
                throw new ArgumentOutOfRangeException(nameof(configuration));
            return new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext()
                .WriteTo.File(path, rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true, fileSizeLimitBytes: size,
                    retainedFileCountLimit: retained, shared: false, buffered: false,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch (Exception)
        {
            ReportFileFailure();
            // The existing Microsoft console provider remains independent of the file sink.
            return new LoggerConfiguration().CreateLogger();
        }
    }

    private static void ReportFileFailure()
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref lastDiagnostic);
        if (previous != 0 && now - previous < 60000) return;
        if (Interlocked.CompareExchange(ref lastDiagnostic, now, previous) != previous) return;
        try { Console.Error.WriteLine("NeximBridge: no se puede escribir el log de archivo. La consola permanece disponible (aviso limitado a uno por minuto)."); }
        catch (IOException) { }
    }
}
