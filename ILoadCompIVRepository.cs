namespace NeximBridge;

public interface ILoadCompIVRepository
{
    Task InsertLoadCompIVDetails(long rawEventId, LoadCompIVData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
