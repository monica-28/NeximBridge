using Microsoft.Data.SqlClient;
using System.Data;

namespace NeximBridge;

public sealed partial class NeximRepository
{
    public async Task InsertPartsUsageDetails(long rawEventId, PartsUsageData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        ArgumentNullException.ThrowIfNull(data);
        await using SqlConnection connection = PartsUsageConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        // Raw was committed before entering this transaction. Any failed detail rolls
        // back the entire normalized batch on disposal, without losing that raw row.
        foreach (PartsUsageDetail detail in data.Details)
        {
            await using SqlCommand command = CreatePartsUsageCommand(rawEventId, data, detail);
            command.Connection = connection;
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateRawEventStatus(long rawEventId, string status,
        CancellationToken cancellationToken = default)
    {
        await using SqlCommand command = CreateRawEventStatusCommand(rawEventId, status);
        await using SqlConnection connection = PartsUsageConnection();
        await connection.OpenAsync(cancellationToken);
        command.Connection = connection;
        object? updatedId = await command.ExecuteScalarAsync(cancellationToken);
        if (updatedId is null or DBNull)
            throw new InvalidOperationException($"No existe raw_event_id={rawEventId} para actualizar su estado.");
    }

    private SqlConnection PartsUsageConnection() => new(configuration["Nexim:SQL:ConnectionString"]
        ?? throw new InvalidOperationException("Falta la configuración Nexim:SQL:ConnectionString."));

    internal static SqlCommand CreateRawEventStatusCommand(long rawEventId, string status)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        if (status is not ("PENDING" or "PROCESSED" or "ERROR"))
            throw new ArgumentException("Estado de procesamiento no válido.", nameof(status));
        SqlCommand command = new("""
            UPDATE dbo.nexim_raw_event SET parse_status = @status
            OUTPUT INSERTED.raw_event_id
            WHERE raw_event_id = @raw_event_id;
            """);
        command.Parameters.Add("@raw_event_id", SqlDbType.BigInt).Value = rawEventId;
        command.Parameters.Add("@status", SqlDbType.VarChar, 20).Value = status;
        return command;
    }

    internal static SqlCommand CreatePartsUsageCommand(long rawEventId, PartsUsageData data, PartsUsageDetail detail)
    {
        SqlCommand command = new("""
            INSERT INTO dbo.nexim_component_event
                (raw_event_id, event_name, event_time_utc, line_name, machine_name, module_no,
                 lane_no, program_name, panel_no, stage_no, slot_no, part_no, avl_name,
                 parts_usage, pickup_count, error_parts, error_reject, reject_parts,
                 dislodged_parts, rescan_count, no_pickup,
                 quantity, feeder_id, reel_id, old_reel_id, new_reel_id, vendor, lot_no, date_code)
            VALUES
                (@raw_event_id, @event_name, @event_time_utc, @line_name, @machine_name, @module_no,
                 @lane_no, @program_name, @panel_no, @stage_no, @slot_no, @part_no, @avl_name,
                 @parts_usage, @pickup_count, @error_parts, @error_reject, @reject_parts,
                 @dislodged_parts, @rescan_count, @no_pickup,
                 @quantity, @feeder_id, @reel_id, @old_reel_id, @new_reel_id, @vendor, @lot_no, @date_code);
            """);
        command.Parameters.Add("@raw_event_id", SqlDbType.BigInt).Value = rawEventId;
        command.Parameters.Add("@event_name", SqlDbType.VarChar, 50).Value = "PARTSUSAGE";
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTime2).Value = data.EventTimeUtc.UtcDateTime;
        command.Parameters.Add("@line_name", SqlDbType.VarChar, 100).Value = data.LineName;
        command.Parameters.Add("@machine_name", SqlDbType.VarChar, 100).Value = data.MachineName;
        command.Parameters.Add("@module_no", SqlDbType.Int).Value = data.ModuleNo;
        command.Parameters.Add("@lane_no", SqlDbType.Int).Value = data.LaneNo;
        command.Parameters.Add("@program_name", SqlDbType.VarChar, 200).Value = data.ProgramName;
        command.Parameters.Add("@panel_no", SqlDbType.VarChar, 100).Value = data.PanelNo;
        command.Parameters.Add("@stage_no", SqlDbType.Int).Value = detail.StageNo;
        command.Parameters.Add("@slot_no", SqlDbType.Int).Value = detail.SlotNo;
        command.Parameters.Add("@part_no", SqlDbType.VarChar, 100).Value = detail.PartName;
        command.Parameters.Add("@avl_name", SqlDbType.VarChar, 200).Value = detail.AvlName;
        command.Parameters.Add("@parts_usage", SqlDbType.Int).Value = detail.PartsUsage;
        command.Parameters.Add("@pickup_count", SqlDbType.Int).Value = detail.PickupCount;
        command.Parameters.Add("@error_parts", SqlDbType.Int).Value = detail.ErrorParts;
        command.Parameters.Add("@error_reject", SqlDbType.Int).Value = detail.ErrorReject;
        command.Parameters.Add("@reject_parts", SqlDbType.Int).Value = detail.RejectParts;
        command.Parameters.Add("@dislodged_parts", SqlDbType.Int).Value = detail.DislodgedParts;
        command.Parameters.Add("@rescan_count", SqlDbType.Int).Value = detail.RescanCount;
        command.Parameters.Add("@no_pickup", SqlDbType.Int).Value = detail.NoPickup;
        // Explicit NULLs: do not populate fields absent from PARTSUSAGE or use defaults.
        command.Parameters.Add("@quantity", SqlDbType.Int).Value = DBNull.Value;
        foreach (string name in new[] { "feeder_id", "reel_id", "old_reel_id", "new_reel_id", "vendor", "lot_no", "date_code" })
            command.Parameters.Add("@" + name, SqlDbType.VarChar, name == "date_code" ? 50 : 100).Value = DBNull.Value;
        return command;
    }
}
