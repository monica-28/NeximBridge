namespace NeximBridge;

public interface IRawEventRepository
{
    Task<long> InsertRawEvent(string eventName, string? seqId, string rawMessage,
        DateTimeOffset receivedAt, string parseStatus, CancellationToken cancellationToken = default);
}
