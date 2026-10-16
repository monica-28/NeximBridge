namespace NeximBridge;

public sealed class McAlarmOnHandler(
    IMcAlarmOnRepository repository,
    ILogger<McAlarmOnHandler> logger)
{
    public async Task HandleAsync(ParsedNeximMessage message, long rawEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        try
        {
            McAlarmOnData data = McAlarmOnParser.Parse(message);
            await repository.InsertMcAlarmOn(rawEventId, data, cancellationToken);
            await repository.UpdateRawEventStatus(rawEventId, "PROCESSED", cancellationToken);
            logger.LogInformation("MCALARMON raw_event_id={RawEventId}: {Count} registros procesados.",
                rawEventId, 1);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error procesando MCALARMON raw_event_id={RawEventId}. Se conserva el raw.", rawEventId);
            try
            {
                // A cancelled receive token must not prevent recording the processing failure.
                using CancellationTokenSource statusDeadline = new(TimeSpan.FromSeconds(10));
                await repository.UpdateRawEventStatus(rawEventId, "ERROR", statusDeadline.Token);
            }
            catch (Exception statusException)
            {
                logger.LogError(statusException, "No se pudo marcar ERROR en raw_event_id={RawEventId}.", rawEventId);
            }
            throw;
        }
    }
}
