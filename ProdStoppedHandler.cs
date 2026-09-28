namespace NeximBridge;

public sealed class ProdStoppedHandler(
    IProdStoppedRepository repository,
    ILogger<ProdStoppedHandler> logger)
{
    public async Task HandleAsync(ParsedNeximMessage message, long rawEventId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        try
        {
            ProdStoppedData data = ProdStoppedParser.Parse(message);
            await repository.InsertProdStopped(rawEventId, data, cancellationToken);
            await repository.UpdateRawEventStatus(rawEventId, "PROCESSED", cancellationToken);
            logger.LogInformation("PRODSTOPPED raw_event_id={RawEventId}: {Count} registros procesados.",
                rawEventId, 1);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error procesando PRODSTOPPED raw_event_id={RawEventId}. Se conserva el raw.", rawEventId);
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
