namespace NeximBridge;

public interface IMcAlarmOnRepository
{
    Task InsertMcAlarmOn(long rawEventId, McAlarmOnData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
