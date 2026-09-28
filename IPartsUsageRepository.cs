namespace NeximBridge;

public interface IPartsUsageRepository
{
    Task InsertPartsUsageDetails(long rawEventId, PartsUsageData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
