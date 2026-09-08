using DynamicTableApi.Models;

namespace DynamicTableApi.Services;

public interface IDynamicTableService
{
    /// <summary>
    /// Clears the target table if asked to, then inserts the supplied rows into it. The INSERT's
    /// column list is generated here from the target table's own structure — the caller sends
    /// data, not SQL.
    /// </summary>
    /// <exception cref="Data.TableNotFoundException">The target table does not exist.</exception>
    /// <exception cref="Data.InvalidInsertRequestException">
    /// The request cannot be turned into an INSERT: bad identifier, ragged rows, or columns that
    /// fit the target neither by name nor by count.
    /// </exception>
    Task<TableInsertResponse> InsertAsync(TableInsertRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the newest date the target already holds, so a caller that cannot see the table can
    /// still work out where to resume from. How that date is found is decided here, from the
    /// target's own columns - the caller never supplies an expression, because it sits outside
    /// this server and anything it sent would be concatenated into a query.
    /// </summary>
    /// <exception cref="Data.TableNotFoundException">The target table does not exist.</exception>
    /// <exception cref="Data.InvalidInsertRequestException">
    /// The target has no column this server can date its rows by.
    /// </exception>
    Task<WatermarkResponse> GetWatermarkAsync(string tableName, CancellationToken cancellationToken);
}