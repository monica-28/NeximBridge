using System.Globalization;

namespace NeximBridge;

public sealed class ComponentHandler(
    NeximRepository repository,
    ILogger<ComponentHandler> logger)
{
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "LOADCOMPIV",
        "CHANGECOMPII",
        "UNLOADCOMP"
    };

    public async Task HandleAsync(
        ParsedNeximMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!SupportedEvents.Contains(message.EventName))
        {
            throw new NeximMessageParseException(
                $"ComponentHandler no admite el evento '{message.EventName}'.");
        }

        ComponentData data = Parse(message.Fields);
        await repository.InsertComponentEvent(
            message.EventName,
            message.SeqID,
            data.EventTimeUtc,
            data.MachineName,
            data.ModuleNo,
            data.StageNo,
            data.SlotNo,
            data.PartNumber,
            data.FeederId,
            data.ReelId,
            data.OldReelId,
            data.NewReelId,
            data.Quantity,
            data.Vendor,
            data.LotNo,
            data.DateCode,
            message.RawMessage,
            cancellationToken);
    }

    private ComponentData Parse(IReadOnlyList<string> fields)
    {
        return new ComponentData(
            GetDateTimeUtc(fields, 2, "event_time_utc"),
            GetText(fields, 3, "machine_name"),
            GetInteger(fields, 4, "module_no"),
            GetInteger(fields, 5, "stage_no"),
            GetInteger(fields, 6, "slot_no"),
            GetText(fields, 7, "part_no"),
            GetText(fields, 8, "feeder_id", required: false),
            GetText(fields, 9, "reel_id", required: false),
            GetText(fields, 10, "old_reel_id", required: false),
            GetText(fields, 11, "new_reel_id", required: false),
            GetDecimal(fields, 12, "quantity", required: false),
            GetText(fields, 13, "vendor", required: false),
            GetText(fields, 14, "lot_no", required: false),
            GetText(fields, 15, "date_code", required: false));
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
                logger.LogError("El evento de componente no contiene {FieldName}; se usará null.", fieldName);
            }

            return null;
        }

        return fields[index];
    }

    private DateTimeOffset? GetDateTimeUtc(IReadOnlyList<string> fields, int index, string fieldName)
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
        bool required)
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

    private sealed record ComponentData(
        DateTimeOffset? EventTimeUtc,
        string? MachineName,
        int? ModuleNo,
        int? StageNo,
        int? SlotNo,
        string? PartNumber,
        string? FeederId,
        string? ReelId,
        string? OldReelId,
        string? NewReelId,
        decimal? Quantity,
        string? Vendor,
        string? LotNo,
        string? DateCode);
}
