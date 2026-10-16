using System.Net.Sockets;

namespace NeximBridge;

public sealed class Worker(
    ILogger<Worker> logger,
    NeximTcpClientService neximTcpClientService,
    NeximFrameReader frameReader,
    NeximSessionInitializer sessionInitializer,
    IConfiguration configuration,
    NeximMessageParser messageParser,
    NeximRepository repository,
    NeximEventProcessor eventProcessor,
    PartsUsageHandler partsUsageHandler,
    PartsUsageAckSender partsUsageAckSender,
    PdErrorHandler pdErrorHandler,
    PdErrorAckSender pdErrorAckSender,
    ProdStoppedHandler prodStoppedHandler,
    ProdStoppedAckSender prodStoppedAckSender,
    ProdCompletedIIHandler prodCompletedIIHandler,
    ProdCompletedIIAckSender prodCompletedIIAckSender,
    ProdStartedHandler prodStartedHandler,
    ProdStartedAckSender prodStartedAckSender,
    RemainingEventProcessor remainingEvents,
    NeximKeepAliveAckBuilder keepAliveAckBuilder) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NeximBridge Worker iniciado en {StartedAt}", DateTimeOffset.Now);

        NeximEventSubscription[] subscriptions = configuration.GetSection("Nexim:Events").Get<NeximEventSubscription[]>() ?? [];
        NetworkStream? initializedStream = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            NetworkStream? networkStream = neximTcpClientService.NetworkStream;
            if (networkStream is null || !neximTcpClientService.IsConnected)
            {
                logger.LogDebug("El cliente TCP de Nexim está desconectado.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            try
            {
                if (!ReferenceEquals(initializedStream, networkStream))
                {
                    initializedStream = null;
                    try
                    {
                        await sessionInitializer.InitializeSubscriptionsAsync(networkStream,
                            subscriptions,
                            configuration["Nexim:MachineName"] ?? "",
                            TimeSpan.FromSeconds(configuration.GetValue("Nexim:InitializationTimeoutSeconds", 10)),
                            stoppingToken);
                        initializedStream = networkStream;
                        logger.LogInformation("SETEV y STARTEV confirmados. Estado: recepción.");
                    }
                    catch
                    {
                        neximTcpClientService.Disconnect(networkStream);
                        throw;
                    }
                }
                string rawMessage = await frameReader.ReadMessageAsync(networkStream, stoppingToken);
                ParsedNeximMessage message = messageParser.Parse(rawMessage);
                bool requiresAck = NeximAckPolicy.RequiresAck(subscriptions, message.EventName);

                if (message.EventName == "PARTSUSAGE")
                {
                    await eventProcessor.ProcessAsync(networkStream, message, requiresAck,
                        partsUsageHandler.HandleAsync, partsUsageAckSender.SendAsync, stoppingToken);
                    continue;
                }

                if (message.EventName == "PDERROR")
                {
                    await eventProcessor.ProcessAsync(networkStream, message, requiresAck,
                        pdErrorHandler.HandleAsync, pdErrorAckSender.SendAsync, stoppingToken);
                    continue;
                }

                if (message.EventName == "PRODSTARTED")
                {
                    await eventProcessor.ProcessAsync(networkStream, message, requiresAck,
                        prodStartedHandler.HandleAsync, prodStartedAckSender.SendAsync, stoppingToken);
                    continue;
                }

                if (message.EventName == "PRODCOMPLETEDII")
                {
                    await eventProcessor.ProcessAsync(networkStream, message, requiresAck,
                        prodCompletedIIHandler.HandleAsync, prodCompletedIIAckSender.SendAsync, stoppingToken);
                    continue;
                }

                if (message.EventName == "PRODSTOPPED")
                {
                    await eventProcessor.ProcessAsync(networkStream, message, requiresAck,
                        prodStoppedHandler.HandleAsync, prodStoppedAckSender.SendAsync, stoppingToken);
                    continue;
                }

                if (RemainingEventProcessor.Supports(message.EventName))
                {
                    await remainingEvents.ProcessAsync(networkStream, message, requiresAck, stoppingToken);
                    continue;
                }

                if (!string.Equals(message.EventName, "KEEPALIVE", StringComparison.Ordinal))
                {
                    logger.LogDebug("Mensaje Nexim ignorado: {EventName}.", message.EventName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(message.SeqID))
                {
                    throw new NeximMessageParseException(
                        "KEEPALIVE debe contener un SeqID para poder responder.");
                }

                await repository.InsertRawEvent(
                    message.EventName,
                    message.SeqID,
                    message.RawMessage,
                    DateTimeOffset.UtcNow,
                    "OK",
                    stoppingToken);

                byte[] acknowledgement = keepAliveAckBuilder.Build(message.SeqID);
                await networkStream.WriteAsync(acknowledgement, stoppingToken);
                await networkStream.FlushAsync(stoppingToken);

                logger.LogInformation(
                    "KEEPALIVE recibido y confirmado con SeqID {SeqID}.",
                    message.SeqID);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Se perdió la conexión TCP con Nexim.");
                neximTcpClientService.Disconnect(networkStream);
            }
            catch (NeximFrameFormatException exception)
            {
                logger.LogError(exception, "El frame recibido de Nexim no es válido.");
                neximTcpClientService.Disconnect(networkStream);
            }
            catch (NeximMessageParseException exception)
            {
                logger.LogError(exception, "El mensaje recibido de Nexim no es válido.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error procesando un mensaje Nexim.");
            }
        }

        logger.LogInformation("NeximBridge Worker detenido.");
    }
}
