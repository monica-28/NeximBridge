namespace NeximBridge;

public interface IPdErrorRepository
{
    Task InsertPdErrorDetails(long rawEventId, PdErrorData data, CancellationToken cancellationToken = default);
    Task UpdateRawEventStatus(long rawEventId, string status, CancellationToken cancellationToken = default);
}
