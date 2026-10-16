using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace NeximBridge;

internal static class RemainingEventBatch
{
    // All commands and their parameters are validated before opening the connection.
    // Raw persistence is deliberately outside this transaction.
    internal static async Task ExecuteAsync(DbConnection connection, IReadOnlyList<SqlCommand> commands,
        CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (SqlCommand template in commands)
            {
                await using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = template.CommandText;
                foreach (SqlParameter source in template.Parameters)
                {
                    DbParameter parameter = command.CreateParameter();
                    parameter.ParameterName = source.ParameterName;
                    parameter.DbType = source.DbType;
                    parameter.Size = source.Size;
                    parameter.Value = source.Value;
                    command.Parameters.Add(parameter);
                }
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Keep the original failure if the connection also fails during rollback.
            try
            {
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
                await transaction.RollbackAsync(deadline.Token);
            }
            catch { /* Disposal also attempts rollback; caller logs the original error. */ }
            throw;
        }
    }
}
