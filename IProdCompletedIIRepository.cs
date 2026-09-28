namespace NeximBridge;

public interface IProdCompletedIIRepository
{
    Task InsertProdCompletedII(long rawEventId, ProdCompletedIIData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
