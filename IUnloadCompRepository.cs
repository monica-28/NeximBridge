namespace NeximBridge;

public interface IUnloadCompRepository
{
    Task InsertUnloadCompDetails(long rawEventId, UnloadCompData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
