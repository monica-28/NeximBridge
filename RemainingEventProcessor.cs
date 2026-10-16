namespace NeximBridge;

// Session-level orchestration for the five new events. Existing handlers stay unchanged.
public sealed class RemainingEventProcessor(
    IRawEventRepository repository,
    LoadCompIVHandler loadCompIV,
    ChangeCompIIHandler changeCompII,
    UnloadCompHandler unloadComp,
    McAlarmOnHandler mcAlarmOn,
    McAlarmOffHandler mcAlarmOff,
    McAlarmOnAckSender alarmOnAck,
    McAlarmOffAckSender alarmOffAck,
    ILogger<RemainingEventProcessor> logger)
{
    public static bool Supports(string name) => name is
        "LOADCOMPIV" or "CHANGECOMPII" or "UNLOADCOMP" or "MCALARMON" or "MCALARMOFF";

    public async Task ProcessAsync(Stream stream, ParsedNeximMessage message, bool requiresAck,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(message.EventName))
            throw new ArgumentException("Evento no soportado.", nameof(message));
        try
        {
            long rawEventId = await repository.InsertRawEvent(message.EventName, message.SeqID,
                message.RawMessage, DateTimeOffset.UtcNow, "PENDING", cancellationToken);
            switch (message.EventName)
            {
                case "LOADCOMPIV": await loadCompIV.HandleAsync(message, rawEventId, cancellationToken); break;
                case "CHANGECOMPII": await changeCompII.HandleAsync(message, rawEventId, cancellationToken); break;
                case "UNLOADCOMP": await unloadComp.HandleAsync(message, rawEventId, cancellationToken); break;
                case "MCALARMON": await mcAlarmOn.HandleAsync(message, rawEventId, cancellationToken); break;
                case "MCALARMOFF": await mcAlarmOff.HandleAsync(message, rawEventId, cancellationToken); break;
            }
        }
        catch
        {
            if (requiresAck && stream.CanWrite)
            {
                try
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
                    await SendAck(1, deadline.Token);
                }
                catch (Exception error)
                {
                    logger.LogError(error, "No se pudo enviar {EventName}_ACK Result=1 SeqID={SeqID}.",
                        message.EventName, message.SeqID);
                }
            }
            throw;
        }
        // Sending failures do not change a successfully persisted event into a rejection.
        if (requiresAck) await SendAck(0, cancellationToken);

        Task SendAck(int result, CancellationToken token) => message.EventName switch
        {
            "MCALARMON" => alarmOnAck.SendAsync(stream, message, result, token),
            "MCALARMOFF" => alarmOffAck.SendAsync(stream, message, result, token),
            _ => throw new NeximAckException($"Sin ACK específico para {message.EventName}; configurar Ack=false.")
        };
    }
}
