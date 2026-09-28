using Microsoft.Data.SqlClient;
using System.Data;

namespace NeximBridge;

public sealed partial class NeximRepository : IProdCompletedIIRepository
{
    public async Task InsertProdCompletedII(long rawEventId, ProdCompletedIIData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rawEventId);
        ArgumentNullException.ThrowIfNull(data);
        await using SqlCommand command = CreateProdCompletedIICommand(rawEventId, data);
        await using SqlConnection connection = new(configuration["Nexim:SQL:ConnectionString"]
            ?? throw new InvalidOperationException("Falta la configuración Nexim:SQL:ConnectionString."));
        await connection.OpenAsync(cancellationToken);
        command.Connection = connection;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static SqlCommand CreateProdCompletedIICommand(long rawEventId, ProdCompletedIIData data)
    {
        SqlCommand command = new("""
            INSERT INTO dbo.nexim_production_event
                (raw_event_id, event_name, event_time_utc, line_name, machine_name, module_no,
                 lane_no, product_mode, program_name, panel_no,
                 cycle_time, status, block_count, block_skip_count, bs_info_bit)
            VALUES
                (@raw_event_id, @event_name, @event_time_utc, @line_name, @machine_name, @module_no,
                 @lane_no, @product_mode, @program_name, @panel_no,
                 @cycle_time, @status, @block_count, @block_skip_count, @bs_info_bit);
            """);
        command.Parameters.Add("@raw_event_id", SqlDbType.BigInt).Value = rawEventId;
        command.Parameters.Add("@event_name", SqlDbType.VarChar, 50).Value = "PRODCOMPLETEDII";
        command.Parameters.Add("@event_time_utc", SqlDbType.DateTime2).Value = data.EventTimeUtc.UtcDateTime;
        Text("line_name", data.LineName, 100); Text("machine_name", data.MachineName, 100);
        command.Parameters.Add("@module_no", SqlDbType.Int).Value = data.ModuleNo;
        command.Parameters.Add("@lane_no", SqlDbType.Int).Value = data.LaneNo;
        command.Parameters.Add("@product_mode", SqlDbType.Int).Value = data.ProductMode;
        Text("program_name", data.ProgramName, 200); Text("panel_no", data.PanelNo, 100);
        command.Parameters.Add("@cycle_time", SqlDbType.Int).Value = data.CycleTime;
        command.Parameters.Add("@block_count", SqlDbType.Int).Value = data.BlockCount;
        command.Parameters.Add("@block_skip_count", SqlDbType.Int).Value = data.BlockSkipCount;
        command.Parameters.Add("@bs_info_bit", SqlDbType.VarChar, -1).Value = data.BSInfoBit;
        // No established PROCESSED meaning for production.status; it is not a wire field.
        command.Parameters.Add("@status", SqlDbType.VarChar, 50).Value = DBNull.Value;
        return command;

        void Text(string name, string value, int size)
        {
            if (value.Length > size)
                throw new NeximMessageParseException($"PRODCOMPLETEDII: {name} excede la longitud SQL de {size} caracteres.");
            command.Parameters.Add("@" + name, SqlDbType.VarChar, size).Value = value;
        }
    }
}
