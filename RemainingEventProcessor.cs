namespace NeximBridge;

// Session-level orchestration for the five new events. Existing handlers stay unchanged.
public sealed class RemainingEventProcessor(
    NeximEventProcessor eventProcessor,
    LoadCompIVHandler loadCompIV,
    ChangeCompIIHandler changeCompII,
    UnloadCompHandler unloadComp,
    McAlarmOnHandler mcAlarmOn,
    McAlarmOffHandler mcAlarmOff,
    McAlarmOnAckSender alarmOnAck,
    McAlarmOffAckSender alarmOffAck)
{
    public static bool Supports(string name) => name is
        "LOADCOMPIV" or "CHANGECOMPII" or "UNLOADCOMP" or "MCALARMON" or "MCALARMOFF";

    public async Task ProcessAsync(Stream stream, ParsedNeximMessage message, bool requiresAck,
        CancellationToken cancellationToken = default)
    {
        if (!Supports(message.EventName))
            throw new ArgumentException("Evento no soportado.", nameof(message));
        await eventProcessor.ProcessAsync(stream, message, requiresAck, Handle, SendAck, cancellationToken);

        Task Handle(ParsedNeximMessage current, long rawEventId, CancellationToken token) => current.EventName switch
        {
            "LOADCOMPIV" => loadCompIV.HandleAsync(current, rawEventId, token),
            "CHANGECOMPII" => changeCompII.HandleAsync(current, rawEventId, token),
            "UNLOADCOMP" => unloadComp.HandleAsync(current, rawEventId, token),
            "MCALARMON" => mcAlarmOn.HandleAsync(current, rawEventId, token),
            "MCALARMOFF" => mcAlarmOff.HandleAsync(current, rawEventId, token),
            _ => throw new ArgumentException("Evento no soportado.")
        };

        Task SendAck(Stream session, ParsedNeximMessage current, int result, CancellationToken token) => current.EventName switch
        {
            "MCALARMON" => alarmOnAck.SendAsync(session, current, result, token),
            "MCALARMOFF" => alarmOffAck.SendAsync(session, current, result, token),
            _ => throw new NeximAckException($"Sin ACK especifico para {current.EventName}; configurar Ack=false.")
        };
    }
}
