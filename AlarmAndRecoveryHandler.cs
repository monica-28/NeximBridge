using System.Globalization;

namespace NeximBridge;

public sealed class AlarmAndRecoveryHandler(
    NeximRepository repository,
    ILogger<AlarmAndRecoveryHandler> logger)
{
    private static readonly HashSet<string> SupportedEvents = new(StringComparer.Ordinal)
    {
        "MCALARMON",
        "MCALARMOFF",
        "RECOVERYUP"
    };

    public async Task HandleAsync(
        ParsedNeximMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!SupportedEvents.Contains(message.EventName))
        {
            throw new NeximMessageParseException(
                $"AlarmAndRecoveryHandler no admite el evento '{message.EventName}'.");
        }

        AlarmData data = Parse(message.Fields);
        await repository.InsertAlarmRecoveryEvent(
            message.EventName,
            message.SeqID,
            data.EventTimeUtc,
            data.LineName,
            data.MachineName,
            data.ModuleNo,
            data.ErrorCode,
            data.SubErrorCode,
            data.Description,
            message.RawMessage,
            cancellationToken);
    }

    private AlarmData Parse(IReadOnlyList<string> fields)
    {
        return new AlarmData(
            GetDateTimeUtc(fields, 2, "event_time_utc"),
            GetText(fields, 3, "line_name"),
            GetText(fields, 4, "machine_name"),
            GetInteger(fields, 5, "module_no"),
            GetText(fields, 6, "error_code"),
            GetText(fields, 7, "sub_error_code"),
            GetText(fields, 8, "description", required: false));
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
                logger.LogError("El evento de alarma no contiene {FieldName}; se usará null.", fieldName);
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

    private sealed record AlarmData(
        DateTimeOffset? EventTimeUtc,
        string? LineName,
        string? MachineName,
        int? ModuleNo,
        string? ErrorCode,
        string? SubErrorCode,
        string? Description);
}
