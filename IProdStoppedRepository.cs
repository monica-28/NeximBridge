namespace NeximBridge;

public interface IProdStoppedRepository
{
    Task InsertProdStopped(long rawEventId, ProdStoppedData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
