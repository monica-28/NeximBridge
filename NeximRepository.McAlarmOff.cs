using Microsoft.Data.SqlClient;
using System.Data;

namespace NeximBridge;

public sealed partial class NeximRepository : IMcAlarmOffRepository
{
    public async Task InsertMcAlarmOff(long rawEventId, McAlarmOffData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        ArgumentNullException.ThrowIfNull(data);
        SqlCommand[] commands = [CreateMcAlarmOffCommand(rawEventId, data)];
        try
        {
            await using SqlConnection connection = new(configuration["Nexim:SQL:ConnectionString"]
                ?? throw new InvalidOperationException("Falta Nexim:SQL:ConnectionString."));
            await RemainingEventBatch.ExecuteAsync(connection, commands, cancellationToken);
        }
        finally { foreach (SqlCommand command in commands) command.Dispose(); }
    }

    internal static SqlCommand CreateMcAlarmOffCommand(long rawEventId, McAlarmOffData data)
    {
        SqlCommand command = new("""
            INSERT INTO dbo.nexim_incident_event
                (raw_event_id, event_name, event_time_utc, line_name, machine_name, module_no,
                 stage_no, slot_no, lane_no, part_no, feeder_id, reel_id,
                 panel_no, program_name, avl_name, error_code, sub_error_code, description,
                 head_no, holder_no, nozzle_no, nozzle_pit_no, board_no, [reference])
            VALUES
                (@raw_event_id, @event_name, @event_time_utc, @line_name, @machine_name, @module_no,
                 @stage_no, @slot_no, @lane_no, @part_no, @feeder_id, @reel_id,
                 @panel_no, @program_name, @avl_name, @error_code, @sub_error_code, @description,
                 @head_no, @holder_no, @nozzle_no, @nozzle_pit_no, @board_no, @reference);
            """);
        command.Parameters.Add("@raw_event_id", SqlDbType.BigInt).Value = rawEventId;
        command.Parameters.Add("@event_name", SqlDbType.VarChar, 50).Value = "MCALARMOFF";
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTime2).Value = data.EventTimeUtc.UtcDateTime;
        command.Parameters.Add("@line_name", SqlDbType.VarChar, 100).Value = data.LineName;
        command.Parameters.Add("@machine_name", SqlDbType.VarChar, 100).Value = data.MachineName;
        command.Parameters.Add("@module_no", SqlDbType.Int).Value = data.ModuleNo;
        command.Parameters.Add("@stage_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@slot_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@lane_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@part_no", SqlDbType.VarChar, 100).Value = DBNull.Value;
        command.Parameters.Add("@feeder_id", SqlDbType.VarChar, 100).Value = DBNull.Value;
        command.Parameters.Add("@reel_id", SqlDbType.VarChar, 100).Value = DBNull.Value;
        command.Parameters.Add("@panel_no", SqlDbType.VarChar, 100).Value = DBNull.Value;
        command.Parameters.Add("@program_name", SqlDbType.VarChar, 200).Value = DBNull.Value;
        command.Parameters.Add("@avl_name", SqlDbType.VarChar, 200).Value = DBNull.Value;
        command.Parameters.Add("@error_code", SqlDbType.VarChar, 100).Value = data.ErrorCode;
        command.Parameters.Add("@sub_error_code", SqlDbType.VarChar, 100).Value = data.SubErrorCode;
        command.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = DBNull.Value;
        command.Parameters.Add("@head_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@holder_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@nozzle_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@nozzle_pit_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@board_no", SqlDbType.Int).Value = DBNull.Value;
        command.Parameters.Add("@reference", SqlDbType.VarChar, 200).Value = DBNull.Value;
        return command;
    }
}
