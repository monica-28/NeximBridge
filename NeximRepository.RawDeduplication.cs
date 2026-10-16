using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace NeximBridge;

public sealed partial class NeximRepository
{
    public async Task<RawEventInsertResult> InsertRawEventOrGetExistingAsync(string eventName, string? seqId,
        string rawMessage, DateTimeOffset receivedAt, CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = new(configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException("Falta Nexim:SQL:ConnectionString."));
        return await InsertRawEventOrGetExistingAsync(connection, eventName, seqId, rawMessage,
            receivedAt, cancellationToken);
    }

    // The connection seam lets tests inject real SqlExceptions without writing to SQL Server.
    internal static async Task<RawEventInsertResult> InsertRawEventOrGetExistingAsync(DbConnection connection,
        string eventName, string? seqId, string rawMessage, DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        byte[]? hash = RawEventDeduplication.ComputeHash(eventName, rawMessage);
        await connection.OpenAsync(cancellationToken);
        await using DbCommand insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO dbo.nexim_raw_event
                (event_name, seq_id, raw_message, received_at, parse_status, raw_hash)
            OUTPUT INSERTED.raw_event_id
            VALUES (@event_name, @seq_id, @raw_message, @received_at, 'PENDING', @raw_hash);
            """;
        AddParameter(insert, "@event_name", DbType.String, eventName, 200);
        AddParameter(insert, "@seq_id", DbType.String, seqId, 100);
        AddParameter(insert, "@raw_message", DbType.String, rawMessage, -1);
        AddParameter(insert, "@received_at", DbType.DateTimeOffset, receivedAt);
        AddParameter(insert, "@raw_hash", DbType.Binary, hash, 32);
        try
        {
            object? id = await insert.ExecuteScalarAsync(cancellationToken);
            if (id is null or DBNull)
                throw new InvalidOperationException("SQL Server no devolvió el raw_event_id insertado.");
            return new(false, Convert.ToInt64(id, CultureInfo.InvariantCulture), "PENDING", eventName, seqId, rawMessage);
        }
        catch (SqlException error) when (hash is not null && error.Number is 2601 or 2627)
        {
            // No preflight SELECT: the existing UNIQUE index arbitrates concurrent inserts.
            await using DbCommand select = connection.CreateCommand();
            select.CommandText = """
                SELECT raw_event_id, parse_status, event_name, seq_id, raw_message
                FROM dbo.nexim_raw_event WHERE raw_hash = @raw_hash;
                """;
            AddParameter(select, "@raw_hash", DbType.Binary, hash, 32);
            await using DbDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            // Another unique constraint may have failed; never mistake it for a retransmission.
            if (!await reader.ReadAsync(cancellationToken)) throw;
            long rawEventId = reader.GetInt64(0);
            string status = reader.IsDBNull(1) ? "" : reader.GetString(1);
            string existingEvent = reader.GetString(2);
            string? existingSeq = reader.IsDBNull(3) ? null : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture);
            string existingRaw = reader.GetString(4);
            if (!string.Equals(existingRaw, rawMessage, StringComparison.Ordinal) ||
                !string.Equals(existingEvent, eventName, StringComparison.Ordinal))
                throw new InvalidOperationException($"raw_hash coincide con un RAW diferente, raw_event_id={rawEventId}.", error);
            return new(true, rawEventId, status, existingEvent, existingSeq, existingRaw);
        }
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object? value, int size = 0)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        if (size != 0) parameter.Size = size;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
