using System.Globalization;

namespace NeximBridge;

public sealed class ProductionHandler(
    NeximRepository repository,
    ILogger<ProductionHandler> logger)
{
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "PRODSTARTED",
        "PRODCOMPLETEDII",
        "PRODSTOPPED"
    };

    public async Task HandleAsync(
        ParsedNeximMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!SupportedEvents.Contains(message.EventName))
        {
            throw new NeximMessageParseException(
                $"ProductionHandler no admite el evento '{message.EventName}'.");
        }

        ProductionData data = Parse(message.Fields);
        await repository.InsertProductionEvent(
            message.EventName,
            message.SeqID,
            data.EventTimeUtc,
            data.LineName,
            data.MachineName,
            data.ModuleNo,
            data.LaneNo,
            data.ProgramName,
            data.PanelNo,
            data.CycleTime,
            message.EventName,
            message.RawMessage,
            cancellationToken);
    }

    private ProductionData Parse(IReadOnlyList<string> fields)
    {
        return new ProductionData(
            GetDateTimeUtc(fields, 2, "event_time_utc"),
            GetText(fields, 3, "line_name"),
            GetText(fields, 4, "machine_name"),
            GetInteger(fields, 5, "module_no"),
            GetInteger(fields, 6, "lane_no"),
            GetText(fields, 7, "program_name"),
            GetText(fields, 8, "panel_no"),
            GetDecimal(fields, 9, "cycle_time", required: false));
    }

    private string? GetText(
        IReadOnlyList<string> fields,
        int index,
        string fieldName,
        bool required = true)
    {
        if (index >= fields.Count || string.IsNullOrWhiteSpace(fields[index]))
        {
            if (required)
            {
                logger.LogError("El mensaje de producción no contiene {FieldName}; se usará null.", fieldName);
            }

            return null;
        }

        return fields[index];
    }

    private DateTimeOffset? GetDateTimeUtc(
        IReadOnlyList<string> fields,
        int index,
        string fieldName)
    {
        string? value = GetText(fields, index, fieldName);
        if (value is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out DateTimeOffset result))
        {
            return result.ToUniversalTime();
        }

        logger.LogError("No se pudo convertir {FieldName}: {Value}; se usará null.", fieldName, value);
        return null;
    }

    private int? GetInteger(IReadOnlyList<string> fields, int index, string fieldName)
    {
        string? value = GetText(fields, index, fieldName);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }

        logger.LogError("No se pudo convertir {FieldName}: {Value}; se usará null.", fieldName, value);
        return null;
    }

    private decimal? GetDecimal(
        IReadOnlyList<string> fields,
        int index,
        string fieldName,
        bool required = true)
    {
        string? value = GetText(fields, index, fieldName, required);
        if (value is null)
        {
            return null;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result))
        {
            return result;
        }

        logger.LogError("No se pudo convertir {FieldName}: {Value}; se usará null.", fieldName, value);
        return null;
    }

    private sealed record ProductionData(
        DateTimeOffset? EventTimeUtc,
        string? LineName,
        string? MachineName,
        int? ModuleNo,
        int? LaneNo,
        string? ProgramName,
        string? PanelNo,
        decimal? CycleTime);
}
