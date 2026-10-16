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

                if (string.Equals(message.EventName, "PARTSUSAGE", StringComparison.Ordinal))
                {
                    try
                    {
                        long rawEventId = await repository.InsertRawEvent(
                            message.EventName, message.SeqID, message.RawMessage,
                            DateTimeOffset.UtcNow, "PENDING", stoppingToken);
                        await partsUsageHandler.HandleAsync(message, rawEventId, stoppingToken);
                    }
                    catch
                    {
                        try
                        {
                            if (requiresAck && networkStream.CanWrite)
                            {
                                using CancellationTokenSource ackDeadline = new(TimeSpan.FromSeconds(10));
                                await partsUsageAckSender.SendAsync(networkStream, message, 1, ackDeadline.Token);
                            }
                        }
                        catch (Exception ackException)
                        {
                            logger.LogError(ackException, "No se pudo enviar PARTSUSAGE_ACK Result=1, SeqID={SeqID}.", message.SeqID);
                        }
                        throw;
                    }
                    // A send failure must not generate Result=1 after successful persistence.
                    if (requiresAck) await partsUsageAckSender.SendAsync(networkStream, message, 0, stoppingToken);
                    continue;
                }

                if (string.Equals(message.EventName, "PDERROR", StringComparison.Ordinal))
                {
                    try
                    {
                        long rawEventId = await repository.InsertRawEvent(
                            message.EventName, message.SeqID, message.RawMessage,
                            DateTimeOffset.UtcNow, "PENDING", stoppingToken);
                        await pdErrorHandler.HandleAsync(message, rawEventId, stoppingToken);
                    }
                    catch
                    {
                        try
                        {
                            if (requiresAck && networkStream.CanWrite)
                            {
                                using CancellationTokenSource ackDeadline = new(TimeSpan.FromSeconds(10));
                                await pdErrorAckSender.SendAsync(networkStream, message, 1, ackDeadline.Token);
                            }
                        }
                        catch (Exception ackException)
                        {
                            logger.LogError(ackException, "No se pudo enviar PDERROR_ACK Result=1, SeqID={SeqID}.", message.SeqID);
                        }
                        throw;
                    }
                    // A failed ACK write does not turn a committed event into a rejection.
                    if (requiresAck) await pdErrorAckSender.SendAsync(networkStream, message, 0, stoppingToken);
                    continue;
                }

                if (string.Equals(message.EventName, "PRODSTARTED", StringComparison.Ordinal))
                {
                    try
                    {
                        long rawEventId = await repository.InsertRawEvent(
                            message.EventName, message.SeqID, message.RawMessage,
                            DateTimeOffset.UtcNow, "PENDING", stoppingToken);
                        await prodStartedHandler.HandleAsync(message, rawEventId, stoppingToken);
                    }
                    catch
                    {
                        try
                        {
                            if (requiresAck && networkStream.CanWrite)
                            {
                                using CancellationTokenSource ackDeadline = new(TimeSpan.FromSeconds(10));
                                await prodStartedAckSender.SendAsync(networkStream, message, 1, ackDeadline.Token);
                            }
                        }
                        catch (Exception ackException)
                        {
                            logger.LogError(ackException, "No se pudo enviar PRODSTARTED_ACK Result=1, SeqID={SeqID}.", message.SeqID);
                        }
                        throw;
                    }
                    // A failed ACK write does not turn a committed event into a rejection.
                    if (requiresAck) await prodStartedAckSender.SendAsync(networkStream, message, 0, stoppingToken);
                    continue;
                }

                if (string.Equals(message.EventName, "PRODCOMPLETEDII", StringComparison.Ordinal))
                {
                    try
                    {
                        long rawEventId = await repository.InsertRawEvent(
                            message.EventName, message.SeqID, message.RawMessage,
                            DateTimeOffset.UtcNow, "PENDING", stoppingToken);
                        await prodCompletedIIHandler.HandleAsync(message, rawEventId, stoppingToken);
                    }
                    catch
                    {
                        try
                        {
                            if (requiresAck && networkStream.CanWrite)
                            {
                                using CancellationTokenSource ackDeadline = new(TimeSpan.FromSeconds(10));
                                await prodCompletedIIAckSender.SendAsync(networkStream, message, 1, ackDeadline.Token);
                            }
                        }
                        catch (Exception ackException)
                        {
                            logger.LogError(ackException, "No se pudo enviar PRODCOMPLETEDII_ACK Result=1, SeqID={SeqID}.", message.SeqID);
                        }
                        throw;
                    }
                    // A failed ACK write does not turn a committed event into a rejection.
                    if (requiresAck) await prodCompletedIIAckSender.SendAsync(networkStream, message, 0, stoppingToken);
                    continue;
                }

                if (string.Equals(message.EventName, "PRODSTOPPED", StringComparison.Ordinal))
                {
                    try
                    {
                        long rawEventId = await repository.InsertRawEvent(
                            message.EventName, message.SeqID, message.RawMessage,
                            DateTimeOffset.UtcNow, "PENDING", stoppingToken);
                        await prodStoppedHandler.HandleAsync(message, rawEventId, stoppingToken);
                    }
                    catch
                    {
                        try
                        {
                            if (requiresAck && networkStream.CanWrite)
                            {
                                using CancellationTokenSource ackDeadline = new(TimeSpan.FromSeconds(10));
                                await prodStoppedAckSender.SendAsync(networkStream, message, 1, ackDeadline.Token);
                            }
                        }
                        catch (Exception ackException)
                        {
                            logger.LogError(ackException, "No se pudo enviar PRODSTOPPED_ACK Result=1, SeqID={SeqID}.", message.SeqID);
                        }
                        throw;
                    }
                    // A failed ACK write does not turn a committed event into a rejection.
                    if (requiresAck) await prodStoppedAckSender.SendAsync(networkStream, message, 0, stoppingToken);
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
