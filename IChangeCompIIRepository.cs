namespace NeximBridge;

public interface IChangeCompIIRepository
{
    Task InsertChangeCompIIDetails(long rawEventId, ChangeCompIIData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
