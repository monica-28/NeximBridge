namespace NeximBridge;

public sealed class PartsUsageHandler(
    IPartsUsageRepository repository,
    ILogger<PartsUsageHandler> logger)
{
    public async Task HandleAsync(ParsedNeximMessage message, long rawEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        try
        {
            PartsUsageData data = PartsUsageParser.Parse(message);
            await repository.InsertPartsUsageDetails(rawEventId, data, cancellationToken);
            await repository.UpdateRawEventStatus(rawEventId, "PROCESSED", cancellationToken);
            logger.LogInformation("PARTSUSAGE raw_event_id={RawEventId}: {Count} registros procesados.",
                rawEventId, data.Details.Count);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error procesando PARTSUSAGE raw_event_id={RawEventId}. Se conserva el raw.", rawEventId);
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
