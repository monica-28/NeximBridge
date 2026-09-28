using System.Globalization;

namespace NeximBridge;

public sealed class PdErrorHandler(
    NeximRepository repository,
    ILogger<PdErrorHandler> logger)
{
    public async Task HandleAsync(
        ParsedNeximMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!string.Equals(message.EventName, "PDERROR", StringComparison.Ordinal))
        {
            throw new NeximMessageParseException(
                $"PdErrorHandler recibió '{message.EventName}' en lugar de PDERROR.");
        }

        PdErrorData data = Parse(message.Fields);
        await repository.InsertIncidentEvent(
            message.EventName,
            message.SeqID,
            data.EventDate,
            data.Line,
            data.Machine,
            data.Module,
            data.Stage,
            data.Slot,
            data.PartNumber,
            data.ErrorCode,
            data.SubErrorCode,
            data.Reference,
            message.RawMessage,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    private PdErrorData Parse(IReadOnlyList<string> fields)
    {
        return new PdErrorData(
            GetDateTime(fields, 2, "fecha/hora"),
            GetText(fields, 3, "línea"),
            GetText(fields, 4, "máquina"),
            GetText(fields, 5, "módulo"),
            GetText(fields, 6, "stage"),
            GetText(fields, 7, "slot"),
            GetText(fields, 8, "part_no"),
            GetText(fields, 9, "error_code"),
            GetText(fields, 10, "sub_error_code"),
            GetText(fields, 11, "referencia", required: false));
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
                logger.LogError("PDERROR no contiene {FieldName}; se usará null.", fieldName);
            }

            return null;
        }

        return fields[index];
    }

    private DateTimeOffset? GetDateTime(
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
            return result;
        }

        logger.LogError(
            "No se pudo convertir {FieldName} de PDERROR: {Value}; se usará null.",
            fieldName,
            value);
        return null;
    }

    private sealed record PdErrorData(
        DateTimeOffset? EventDate,
        string? Line,
        string? Machine,
        string? Module,
        string? Stage,
        string? Slot,
        string? PartNumber,
        string? ErrorCode,
        string? SubErrorCode,
        string? Reference);
}
