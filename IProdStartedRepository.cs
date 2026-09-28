namespace NeximBridge;

public interface IProdStartedRepository
{
    Task InsertProdStarted(long rawEventId, ProdStartedData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
