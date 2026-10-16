namespace NeximBridge;

public interface IMcAlarmOffRepository
{
    Task InsertMcAlarmOff(long rawEventId, McAlarmOffData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
