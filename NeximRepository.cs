using Microsoft.Data.SqlClient;
using System.Data;

namespace NeximBridge;

public sealed partial class NeximRepository(IConfiguration configuration) : IPartsUsageRepository, IRawEventRepository
{
    internal static object SqlSequence(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => c is < '0' or > '9') ||
            !int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id) ||
            id is < 1 or > 999999) return DBNull.Value;
        return id;
    }

    private const string InsertRawEventCommand = """
        INSERT INTO nexim_raw_event
            (event_name, seq_id, raw_message, received_at, parse_status)
        OUTPUT INSERTED.raw_event_id
        VALUES
            (@event_name, @seq_id, @raw_message, @received_at, @parse_status);
        """;

    public async Task<long> InsertRawEvent(
        string eventName,
        string? seqId,
        string rawMessage,
        DateTimeOffset receivedAt,
        string parseStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(rawMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(parseStatus);

        string connectionString = configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Falta la configuración Nexim:SQL:ConnectionString.");

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using SqlCommand command = new(InsertRawEventCommand, connection);
        command.Parameters.Add("@event_name", SqlDbType.NVarChar, 200).Value = eventName;
        command.Parameters.Add("@seq_id", SqlDbType.Int).Value = SqlSequence(seqId);
        command.Parameters.Add("@raw_message", SqlDbType.NVarChar, -1).Value = rawMessage;
        command.Parameters.Add("@received_at", SqlDbType.DateTimeOffset).Value = receivedAt;
        command.Parameters.Add("@parse_status", SqlDbType.NVarChar, 50).Value = parseStatus;

        object? insertedId = await command.ExecuteScalarAsync(cancellationToken);
        if (insertedId is null || insertedId is DBNull)
        {
            throw new InvalidOperationException(
                "SQL Server no devolvió el raw_event_id insertado.");
        }

        return Convert.ToInt64(insertedId);
    }

}
