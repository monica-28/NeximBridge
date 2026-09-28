using Microsoft.Data.SqlClient;
using System.Data;

namespace NeximBridge;

public sealed partial class NeximRepository : IPdErrorRepository
{
    public async Task InsertPdErrorDetails(long rawEventId, PdErrorData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        ArgumentNullException.ThrowIfNull(data);
        await using SqlConnection connection = new(configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException("Falta la configuración Nexim:SQL:ConnectionString."));
        await connection.OpenAsync(cancellationToken);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        // Raw is already committed. Dispose rolls back the entire detail batch on failure.
        foreach (PdErrorDetail detail in data.Details)
        {
            await using SqlCommand command = CreatePdErrorCommand(rawEventId, data, detail);
            command.Connection = connection;
            command.Transaction = transaction;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    internal static SqlCommand CreatePdErrorCommand(long rawEventId, PdErrorData data, PdErrorDetail detail)
    {
        SqlCommand command = new("""
            INSERT INTO dbo.nexim_incident_event
                (raw_event_id, event_name, event_time_utc, line_name, machine_name, module_no,
                 lane_no, program_name, panel_no, stage_no, slot_no, part_no, avl_name,
                 head_no, holder_no, nozzle_no, nozzle_pit_no, board_no, [reference],
                 error_code, sub_error_code, reel_id, feeder_id, description)
            VALUES
                (@raw_event_id, @event_name, @event_time_utc, @line_name, @machine_name, @module_no,
                 @lane_no, @program_name, @panel_no, @stage_no, @slot_no, @part_no, @avl_name,
                 @head_no, @holder_no, @nozzle_no, @nozzle_pit_no, @board_no, @reference,
                 @error_code, @sub_error_code, @reel_id, @feeder_id, @description);
            """);
        command.Parameters.Add("@raw_event_id", SqlDbType.BigInt).Value = rawEventId;
        command.Parameters.Add("@event_name", SqlDbType.VarChar, 50).Value = "PDERROR";
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTime2).Value = data.EventTimeUtc.UtcDateTime;
        Text("line_name", data.LineName, 100); Text("machine_name", data.MachineName, 100);
        Number("module_no", data.ModuleNo); Number("lane_no", data.LaneNo);
        Text("program_name", data.ProgramName, 200); Text("panel_no", null, 100);
        Number("stage_no", detail.StageNo); Number("slot_no", detail.SlotNo);
        Text("part_no", detail.PartName, 100); Text("avl_name", detail.AvlName, 200);
        Number("head_no", detail.HeadNo); Number("holder_no", detail.HolderNo);
        Number("nozzle_no", detail.NozzleNo); Number("nozzle_pit_no", detail.NozzlePitNo);
        Number("board_no", detail.BoardNo); Text("reference", detail.Reference, 200);
        Text("error_code", detail.ErrorCode, 100); Text("sub_error_code", detail.SubErrorCode, 100);
        Text("reel_id", null, 100); Text("feeder_id", null, 100);
        command.Parameters.Add("@description", SqlDbType.NVarChar, 500).Value = DBNull.Value;
        return command;

        void Number(string name, int value) => command.Parameters.Add("@" + name, SqlDbType.Int).Value = value;
        void Text(string name, string? value, int size)
        {
            if (value?.Length > size)
                throw new NeximMessageParseException($"PDERROR: {name} excede la longitud SQL de {size} caracteres.");
            command.Parameters.Add("@" + name, SqlDbType.VarChar, size).Value = (object?)value ?? DBNull.Value;
        }
    }
}
