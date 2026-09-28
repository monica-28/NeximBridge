using Microsoft.Data.SqlClient;
using System.Data;

namespace NeximBridge;

public sealed partial class NeximRepository(IConfiguration configuration) : IPartsUsageRepository
{
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
        command.Parameters.Add("@seq_id", SqlDbType.NVarChar, 100).Value =
            (object?)seqId ?? DBNull.Value;
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

    public async Task InsertComponentEvent(
        string eventName,
        string? seqId,
        DateTimeOffset? eventTimeUtc,
        string? machineName,
        int? moduleNo,
        int? stageNo,
        int? slotNo,
        string? partNumber,
        string? feederId,
        string? reelId,
        string? oldReelId,
        string? newReelId,
        decimal? quantity,
        string? vendor,
        string? lotNo,
        string? dateCode,
        string rawMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(rawMessage);

        string connectionString = configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Falta la configuración Nexim:SQL:ConnectionString.");

        const string commandText = """
            INSERT INTO nexim_component_event
                (event_name, seq_id, event_time_utc, machine_name, module_no, stage_no, slot_no,
                 part_no, feeder_id, reel_id, old_reel_id, new_reel_id, quantity, vendor, lot_no,
                 date_code, raw_message)
            VALUES
                (@event_name, @seq_id, @event_time_utc, @machine_name, @module_no, @stage_no, @slot_no,
                 @part_no, @feeder_id, @reel_id, @old_reel_id, @new_reel_id, @quantity, @vendor, @lot_no,
                 @date_code, @raw_message);
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqlCommand command = new(commandText, connection);
        command.Parameters.Add("@event_name", SqlDbType.NVarChar, 200).Value = eventName;
        command.Parameters.Add("@seq_id", SqlDbType.NVarChar, 100).Value =
            (object?)seqId ?? DBNull.Value;
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTimeOffset).Value =
            (object?)eventTimeUtc ?? DBNull.Value;
        command.Parameters.Add("@machine_name", SqlDbType.NVarChar, 200).Value =
            (object?)machineName ?? DBNull.Value;
        command.Parameters.Add("@module_no", SqlDbType.Int).Value = (object?)moduleNo ?? DBNull.Value;
        command.Parameters.Add("@stage_no", SqlDbType.Int).Value = (object?)stageNo ?? DBNull.Value;
        command.Parameters.Add("@slot_no", SqlDbType.Int).Value = (object?)slotNo ?? DBNull.Value;
        command.Parameters.Add("@part_no", SqlDbType.NVarChar, 200).Value =
            (object?)partNumber ?? DBNull.Value;
        command.Parameters.Add("@feeder_id", SqlDbType.NVarChar, 200).Value =
            (object?)feederId ?? DBNull.Value;
        command.Parameters.Add("@reel_id", SqlDbType.NVarChar, 200).Value =
            (object?)reelId ?? DBNull.Value;
        command.Parameters.Add("@old_reel_id", SqlDbType.NVarChar, 200).Value =
            (object?)oldReelId ?? DBNull.Value;
        command.Parameters.Add("@new_reel_id", SqlDbType.NVarChar, 200).Value =
            (object?)newReelId ?? DBNull.Value;
        command.Parameters.Add("@quantity", SqlDbType.Decimal).Value = (object?)quantity ?? DBNull.Value;
        command.Parameters.Add("@vendor", SqlDbType.NVarChar, 200).Value =
            (object?)vendor ?? DBNull.Value;
        command.Parameters.Add("@lot_no", SqlDbType.NVarChar, 200).Value =
            (object?)lotNo ?? DBNull.Value;
        command.Parameters.Add("@date_code", SqlDbType.NVarChar, 100).Value =
            (object?)dateCode ?? DBNull.Value;
        command.Parameters.Add("@raw_message", SqlDbType.NVarChar, -1).Value = rawMessage;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertIncidentEvent(
        string eventName,
        string? seqId,
        DateTimeOffset? eventDate,
        string? line,
        string? machine,
        string? module,
        string? stage,
        string? slot,
        string? partNumber,
        string? errorCode,
        string? subErrorCode,
        string? reference,
        string rawMessage,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(rawMessage);

        string connectionString = configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Falta la configuración Nexim:SQL:ConnectionString.");

        const string commandText = """
            INSERT INTO nexim_incident_event
                (event_name, seq_id, event_at, line, machine, module, stage, slot,
                 part_no, error_code, sub_error_code, reference, raw_message, received_at)
            VALUES
                (@event_name, @seq_id, @event_at, @line, @machine, @module, @stage, @slot,
                 @part_no, @error_code, @sub_error_code, @reference, @raw_message, @received_at);
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqlCommand command = new(commandText, connection);
        command.Parameters.Add("@event_name", SqlDbType.NVarChar, 200).Value = eventName;
        command.Parameters.Add("@seq_id", SqlDbType.NVarChar, 100).Value =
            (object?)seqId ?? DBNull.Value;
        command.Parameters.Add("@event_at", SqlDbType.DateTimeOffset).Value =
            (object?)eventDate ?? DBNull.Value;
        command.Parameters.Add("@line", SqlDbType.NVarChar, 100).Value =
            (object?)line ?? DBNull.Value;
        command.Parameters.Add("@machine", SqlDbType.NVarChar, 200).Value =
            (object?)machine ?? DBNull.Value;
        command.Parameters.Add("@module", SqlDbType.NVarChar, 200).Value =
            (object?)module ?? DBNull.Value;
        command.Parameters.Add("@stage", SqlDbType.NVarChar, 100).Value =
            (object?)stage ?? DBNull.Value;
        command.Parameters.Add("@slot", SqlDbType.NVarChar, 100).Value =
            (object?)slot ?? DBNull.Value;
        command.Parameters.Add("@part_no", SqlDbType.NVarChar, 200).Value =
            (object?)partNumber ?? DBNull.Value;
        command.Parameters.Add("@error_code", SqlDbType.NVarChar, 100).Value =
            (object?)errorCode ?? DBNull.Value;
        command.Parameters.Add("@sub_error_code", SqlDbType.NVarChar, 100).Value =
            (object?)subErrorCode ?? DBNull.Value;
        command.Parameters.Add("@reference", SqlDbType.NVarChar, 500).Value =
            (object?)reference ?? DBNull.Value;
        command.Parameters.Add("@raw_message", SqlDbType.NVarChar, -1).Value = rawMessage;
        command.Parameters.Add("@received_at", SqlDbType.DateTimeOffset).Value = receivedAt;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertProductionEvent(
        string eventName,
        string? seqId,
        DateTimeOffset? eventTimeUtc,
        string? lineName,
        string? machineName,
        int? moduleNo,
        int? laneNo,
        string? programName,
        string? panelNo,
        decimal? cycleTime,
        string status,
        string rawMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(rawMessage);

        string connectionString = configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Falta la configuración Nexim:SQL:ConnectionString.");

        const string commandText = """
            INSERT INTO nexim_production_event
                (event_name, seq_id, event_time_utc, line_name, machine_name, module_no,
                 lane_no, program_name, panel_no, cycle_time, status, raw_message)
            VALUES
                (@event_name, @seq_id, @event_time_utc, @line_name, @machine_name, @module_no,
                 @lane_no, @program_name, @panel_no, @cycle_time, @status, @raw_message);
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqlCommand command = new(commandText, connection);
        command.Parameters.Add("@event_name", SqlDbType.NVarChar, 200).Value = eventName;
        command.Parameters.Add("@seq_id", SqlDbType.NVarChar, 100).Value =
            (object?)seqId ?? DBNull.Value;
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTimeOffset).Value =
            (object?)eventTimeUtc ?? DBNull.Value;
        command.Parameters.Add("@line_name", SqlDbType.NVarChar, 200).Value =
            (object?)lineName ?? DBNull.Value;
        command.Parameters.Add("@machine_name", SqlDbType.NVarChar, 200).Value =
            (object?)machineName ?? DBNull.Value;
        command.Parameters.Add("@module_no", SqlDbType.Int).Value =
            (object?)moduleNo ?? DBNull.Value;
        command.Parameters.Add("@lane_no", SqlDbType.Int).Value =
            (object?)laneNo ?? DBNull.Value;
        command.Parameters.Add("@program_name", SqlDbType.NVarChar, 200).Value =
            (object?)programName ?? DBNull.Value;
        command.Parameters.Add("@panel_no", SqlDbType.NVarChar, 200).Value =
            (object?)panelNo ?? DBNull.Value;
        command.Parameters.Add("@cycle_time", SqlDbType.Decimal).Value =
            (object?)cycleTime ?? DBNull.Value;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = status;
        command.Parameters.Add("@raw_message", SqlDbType.NVarChar, -1).Value = rawMessage;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task InsertAlarmRecoveryEvent(
        string eventName,
        string? seqId,
        DateTimeOffset? eventTimeUtc,
        string? lineName,
        string? machineName,
        int? moduleNo,
        string? errorCode,
        string? subErrorCode,
        string? description,
        string rawMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(rawMessage);

        string connectionString = configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Falta la configuración Nexim:SQL:ConnectionString.");

        const string commandText = """
            INSERT INTO nexim_alarm_recovery_event
                (event_name, seq_id, event_time_utc, line_name, machine_name, module_no,
                 error_code, sub_error_code, description, raw_message)
            VALUES
                (@event_name, @seq_id, @event_time_utc, @line_name, @machine_name, @module_no,
                 @error_code, @sub_error_code, @description, @raw_message);
            """;

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqlCommand command = new(commandText, connection);
        command.Parameters.Add("@event_name", SqlDbType.NVarChar, 200).Value = eventName;
        command.Parameters.Add("@seq_id", SqlDbType.NVarChar, 100).Value =
            (object?)seqId ?? DBNull.Value;
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTimeOffset).Value =
            (object?)eventTimeUtc ?? DBNull.Value;
        command.Parameters.Add("@line_name", SqlDbType.NVarChar, 200).Value =
            (object?)lineName ?? DBNull.Value;
        command.Parameters.Add("@machine_name", SqlDbType.NVarChar, 200).Value =
            (object?)machineName ?? DBNull.Value;
        command.Parameters.Add("@module_no", SqlDbType.Int).Value = (object?)moduleNo ?? DBNull.Value;
        command.Parameters.Add("@error_code", SqlDbType.NVarChar, 100).Value =
            (object?)errorCode ?? DBNull.Value;
        command.Parameters.Add("@sub_error_code", SqlDbType.NVarChar, 100).Value =
            (object?)subErrorCode ?? DBNull.Value;
        command.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value =
            (object?)description ?? DBNull.Value;
        command.Parameters.Add("@raw_message", SqlDbType.NVarChar, -1).Value = rawMessage;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}