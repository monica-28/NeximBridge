using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeximBridge;
using Microsoft.Extensions.Configuration;
using Serilog;

try
{
IConfigurationRoot loggingConfiguration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables().AddCommandLine(args).Build();
using var configurationLifetime = (IDisposable)loggingConfiguration;
using var fileLogger = NeximLogging.CreateFileLogger(loggingConfiguration);
using var startupFactory = LoggerFactory.Create(logging => logging
    .AddConfiguration(loggingConfiguration.GetSection("Logging"))
    .AddSimpleConsole().AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(fileLogger, dispose: false)));
var startupLogger = startupFactory.CreateLogger("NeximBridge.Startup");
try
{
using IHost host = Host.CreateDefaultBuilder(args)
	.UseWindowsService(options => options.ServiceName = "NeximBridge")
	.ConfigureLogging(logging =>
	{
        logging.AddConfiguration(loggingConfiguration.GetSection("Logging"));
        logging.AddProvider(new Serilog.Extensions.Logging.SerilogLoggerProvider(fileLogger, dispose: false));
		if (OperatingSystem.IsWindows())
		{
			logging.AddEventLog(options => options.SourceName = "NeximBridge");
		}
	})
	.ConfigureServices(services =>
	{
		services.AddSingleton<NeximTcpClientService>();
		services.AddSingleton<NeximRepository>();
        services.AddSingleton<IPartsUsageRepository>(provider => provider.GetRequiredService<NeximRepository>());
		services.AddSingleton<PartsUsageHandler>();
        services.AddSingleton<IRawEventRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<NeximEventProcessor>();
        services.AddSingleton<RemainingEventProcessor>();
        services.AddSingleton<ILoadCompIVRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<LoadCompIVHandler>();
        services.AddSingleton<IChangeCompIIRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<ChangeCompIIHandler>();
        services.AddSingleton<IUnloadCompRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<UnloadCompHandler>();
        services.AddSingleton<IMcAlarmOnRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<McAlarmOnHandler>();
        services.AddSingleton<McAlarmOnAckSender>();
        services.AddSingleton<IMcAlarmOffRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<McAlarmOffHandler>();
        services.AddSingleton<McAlarmOffAckSender>();
        services.AddSingleton<NeximAckBuilder>();
        services.AddSingleton<PartsUsageAckSender>();
		services.AddSingleton<PdErrorHandler>();
        services.AddSingleton<IPdErrorRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<PdErrorAckSender>();
        services.AddSingleton<IProdStartedRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<ProdStartedHandler>();
        services.AddSingleton<IProdCompletedIIRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<ProdCompletedIIHandler>();
        services.AddSingleton<IProdStoppedRepository>(provider => provider.GetRequiredService<NeximRepository>());
        services.AddSingleton<ProdStoppedHandler>();
        services.AddSingleton<ProdStoppedAckSender>();
        services.AddSingleton<ProdCompletedIIAckSender>();
        services.AddSingleton<ProdStartedAckSender>();
		services.AddSingleton<NeximFrameReader>();
        services.AddSingleton<NeximSetEventBuilder>();
        services.AddSingleton<NeximStartEventBuilder>();
        services.AddSingleton<NeximSessionInitializer>();
		services.AddSingleton<NeximMessageParser>();
		services.AddSingleton<NeximKeepAliveAckBuilder>();
		services.AddHostedService(serviceProvider =>
			serviceProvider.GetRequiredService<NeximTcpClientService>());
		services.AddHostedService<Worker>();
	})
	.Build();

await host.RunAsync();
}
catch (Exception exception)
{
    startupLogger.LogCritical(exception, "NeximBridge no pudo arrancar o terminó inesperadamente.");
    Environment.ExitCode = 1;
}
// Disposing the host first, then the factories and finally the sink flushes/closes files.
}
catch (Exception exception)
{
    Console.Error.WriteLine($"NeximBridge: error inicializando logging ({exception.GetType().Name}).");
    Environment.ExitCode = 1;
}
