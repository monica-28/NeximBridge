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
