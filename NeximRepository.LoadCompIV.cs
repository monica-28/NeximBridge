using Microsoft.Data.SqlClient;
using System.Data;

namespace NeximBridge;

public sealed partial class NeximRepository : ILoadCompIVRepository
{
    public async Task InsertLoadCompIVDetails(long rawEventId, LoadCompIVData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        ArgumentNullException.ThrowIfNull(data);
        SqlCommand[] commands = data.Details.Select(detail => CreateLoadCompIVCommand(rawEventId, data, detail)).ToArray();
        try
        {
            await using SqlConnection connection = new(configuration["Nexim:SQL:ConnectionString"]
                ?? throw new InvalidOperationException("Falta Nexim:SQL:ConnectionString."));
            await RemainingEventBatch.ExecuteAsync(connection, commands, cancellationToken);
        }
        finally { foreach (SqlCommand command in commands) command.Dispose(); }
    }

    internal static SqlCommand CreateLoadCompIVCommand(long rawEventId, LoadCompIVData data, LoadCompIVDetail detail)
    {
        SqlCommand command = new("""
            INSERT INTO dbo.nexim_component_event
                (raw_event_id, event_name, event_time_utc, line_name, machine_name, module_no,
                 stage_no, slot_no, quantity, parts_usage, pickup_count, error_parts,
                 reject_parts, no_pickup, lane_no, error_reject, dislodged_parts, rescan_count,
                 supply_method, remaining_time, part_no, feeder_id, reel_id, old_reel_id,
                 new_reel_id, vendor, lot_no, panel_no, lighting_class, date_code,
                 program_name, avl_name)
            VALUES
                (@raw_event_id, @event_name, @event_time_utc, @line_name, @machine_name, @module_no,
                 @stage_no, @slot_no, @quantity, @parts_usage, @pickup_count, @error_parts,
                 @reject_parts, @no_pickup, @lane_no, @error_reject, @dislodged_parts, @rescan_count,
                 @supply_method, @remaining_time, @part_no, @feeder_id, @reel_id, @old_reel_id,
                 @new_reel_id, @vendor, @lot_no, @panel_no, @lighting_class, @date_code,
                 @program_name, @avl_name);
            """);
        command.Parameters.Add("@raw_event_id", SqlDbType.BigInt).Value = rawEventId;
        command.Parameters.Add("@event_name", SqlDbType.VarChar, 50).Value = "LOADCOMPIV";
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTime2).Value = data.EventTimeUtc.UtcDateTime;
        command.Parameters.Add("@line_name", SqlDbType.VarChar, 100).Value = data.LineName;
        command.Parameters.Add("@machine_name", SqlDbType.VarChar, 100).Value = data.MachineName;
        command.Parameters.Add("@module_no", SqlDbType.Int).Value = data.ModuleNo;
        command.Parameters.Add("@stage_no", SqlDbType.Int).Value = detail.StageNo;
        command.Parameters.Add("@slot_no", SqlDbType.Int).Value = detail.SlotNo;
        command.Parameters.Add("@quantity", SqlDbType.Int).Value = detail.Quantity;
        command.Parameters.Add("@parts_usage", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@pickup_count", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@error_parts", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@reject_parts", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@no_pickup", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@lane_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@error_reject", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@dislodged_parts", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@rescan_count", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@supply_method", SqlDbType.Int).Value = detail.SupplyMethod;
        command.Parameters.Add("@remaining_time", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@part_no", SqlDbType.VarChar, 100).Value = detail.PartNo;
        command.Parameters.Add("@feeder_id", SqlDbType.VarChar, 100).Value = detail.FeederID;
        command.Parameters.Add("@reel_id", SqlDbType.VarChar, 100).Value = detail.ReelID;
        command.Parameters.Add("@old_reel_id", SqlDbType.VarChar, 100).Value = DBNull.Value;
        command.Parameters.Add("@new_reel_id", SqlDbType.VarChar, 100).Value = DBNull.Value;
        command.Parameters.Add("@vendor", SqlDbType.VarChar, 100).Value = detail.Vendor;
        command.Parameters.Add("@lot_no", SqlDbType.VarChar, 100).Value = detail.LotNo;
        command.Parameters.Add("@panel_no", SqlDbType.VarChar, 100).Value = DBNull.Value;
        command.Parameters.Add("@lighting_class", SqlDbType.VarChar, 100).Value = detail.LightingClass;
        command.Parameters.Add("@date_code", SqlDbType.VarChar, 50).Value = detail.DateCode;
        command.Parameters.Add("@program_name", SqlDbType.VarChar, 200).Value = DBNull.Value;
        command.Parameters.Add("@avl_name", SqlDbType.VarChar, 200).Value = DBNull.Value;
        return command;
    }
}
