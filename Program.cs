using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeximBridge;

IHost host = Host.CreateDefaultBuilder(args)
	.UseWindowsService(options => options.ServiceName = "NeximBridge")
	.ConfigureLogging(logging =>
	{
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
        services.AddSingleton<NeximAckBuilder>();
        services.AddSingleton<PartsUsageAckSender>();
		services.AddSingleton<PdErrorHandler>();
		services.AddSingleton<ProductionHandler>();
		services.AddSingleton<ComponentHandler>();
		services.AddSingleton<AlarmAndRecoveryHandler>();
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
