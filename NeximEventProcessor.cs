namespace NeximBridge;

public sealed class NeximEventProcessor(IRawEventRepository repository, ILogger<NeximEventProcessor> logger)
{
    public async Task ProcessAsync(Stream stream, ParsedNeximMessage message, bool requiresAck,
        Func<ParsedNeximMessage, long, CancellationToken, Task> handle,
        Func<Stream, ParsedNeximMessage, int, CancellationToken, Task> sendAck,
        CancellationToken cancellationToken = default)
    {
        if (!RawEventDeduplication.Supports(message.EventName))
            throw new ArgumentException("Evento fuera de la política de deduplicación.", nameof(message));

        int result;
        try
        {
            RawEventInsertResult raw = await repository.InsertRawEventOrGetExistingAsync(
                message.EventName, message.SeqID, message.RawMessage, DateTimeOffset.UtcNow, cancellationToken);
            if (!raw.IsDuplicate)
            {
                logger.LogInformation("RAW event inserted Event={EventName} SeqID={SeqId} RawEventId={RawEventId}",
                    message.EventName, message.SeqID, raw.RawEventId);
                await handle(message, raw.RawEventId, cancellationToken);
                result = 0;
            }
            else if (raw.ParseStatus == "PROCESSED")
            {
                logger.LogInformation("Duplicate event ignored Event={EventName} SeqID={SeqId} RawEventId={RawEventId} Status=PROCESSED",
                    message.EventName, message.SeqID, raw.RawEventId);
                result = 0;
            }
            else
            {
                // PENDING, ERROR and unknown states all require manual recovery, never replay.
                logger.LogWarning("Duplicate event not reprocessed Event={EventName} SeqID={SeqId} RawEventId={RawEventId} Status={Status}",
                    message.EventName, message.SeqID, raw.RawEventId, raw.ParseStatus);
                result = 1;
            }
        }
        catch
        {
            if (requiresAck && stream.CanWrite)
            {
                try
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
                    await sendAck(stream, message, 1, deadline.Token);
                }
                catch (Exception error)
                {
                    logger.LogError(error, "No se pudo enviar {EventName}_ACK Result=1 SeqID={SeqID}.",
                        message.EventName, message.SeqID);
                }
            }
            throw;
        }

        // In particular, an ACK failure on a duplicate cannot update or reprocess its RAW.
        if (requiresAck) await sendAck(stream, message, result, cancellationToken);
    }
}
