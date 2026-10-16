namespace NeximBridge;

public interface IRawEventRepository
{
    Task<RawEventInsertResult> InsertRawEventOrGetExistingAsync(string eventName, string? seqId,
        string rawMessage, DateTimeOffset receivedAt, CancellationToken cancellationToken = default);

    Task<long> InsertRawEvent(string eventName, string? seqId, string rawMessage,
        DateTimeOffset receivedAt, string parseStatus, CancellationToken cancellationToken = default);
}
