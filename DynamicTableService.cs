using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicTableApi.Data;
using DynamicTableApi.Models;
using Microsoft.Data.SqlClient;

namespace DynamicTableApi.Services;

/// <summary>
/// Owns the whole write path. Nothing about the target is configured or hard coded: the
/// structure for the INSERT is read from the table named in the request, the caller's columns
/// are paired with it by name when the names match and by position otherwise, and the values
/// are converted to the target's own SQL types before they are bound as parameters.
///
/// The pre-load clear and the insert always share one transaction, so clearing a target and
/// then failing to refill it is not a possible outcome.
/// </summary>
public class DynamicTableService : IDynamicTableService
{
    /// <summary>SQL Server allows 2100 parameters per command; leave room for the rest.</summary>
    private const int MaxParametersPerBatch = 2000;

    /// <summary>A single INSERT ... VALUES statement accepts at most 1000 row constructors.</summary>
    private const int MaxRowsPerBatch = 1000;

    private static readonly Regex ParameterNamePattern =
        new(@"^@[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly int _commandTimeoutSeconds;
    private readonly HashSet<string> _watermarkIgnoreColumns;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DynamicTableService> _logger;

    public DynamicTableService(IConfiguration configuration, ILogger<DynamicTableService> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection is not configured in appsettings.json");

        _commandTimeoutSeconds = configuration.GetValue("Api:CommandTimeoutSeconds", 600);
        _configuration = configuration;
        _logger = logger;

        // Load-audit columns record when this API wrote a row, not when the business event
        // happened, so dating a target by one would walk the window forward every pass.
        _watermarkIgnoreColumns = new HashSet<string>(
            (configuration.GetValue("Api:WatermarkIgnoreColumns", "EntryDateTime") ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .Where(name => name.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<WatermarkResponse> GetWatermarkAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        var target = SqlIdentifier.QuoteName(tableName, "tableName");

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureInsertableObjectAsync(connection, target, cancellationToken);

        var expression = await ResolveWatermarkExpressionAsync(connection, tableName, target, cancellationToken);
        var sql = $"SELECT {expression} FROM {target};";

        object? value;
        using (var command = new SqlCommand(sql, connection))
        {
            command.CommandTimeout = _commandTimeoutSeconds;
            value = await command.ExecuteScalarAsync(cancellationToken);
        }

        DateTime? watermark = null;
        if (value is not null && value != DBNull.Value)
        {
            try
            {
                watermark = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidInsertRequestException(
                    $"Watermark expression '{expression}' on {target} returned '{value}', which is not a date.");
            }
        }

        _logger.LogInformation(
            "{Target}: watermark {Expression} = {Watermark}.",
            target, expression, watermark?.ToString("yyyy-MM-dd") ?? "none");

        return new WatermarkResponse
        {
            TableName = tableName,
            Watermark = watermark,
            Expression = expression
        };
    }

    /// <summary>
    /// A per-table override from this server's own configuration wins; otherwise the expression is
    /// derived from the target's columns. Nothing here comes from the request - the caller is a
    /// different machine, and an expression it supplied would be concatenated straight into SQL.
    /// </summary>
    private async Task<string> ResolveWatermarkExpressionAsync(
        SqlConnection connection,
        string tableName,
        string quotedTarget,
        CancellationToken cancellationToken)
    {
        var key = SqlIdentifier.Unquote(tableName.Split('.').Last());
        var configured = _configuration[$"Api:WatermarkExpressions:{key}"];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        var columns = await LoadAllColumnsAsync(connection, quotedTarget, cancellationToken);

        var year = columns.FirstOrDefault(c =>
            c.Name.Equals("Year", StringComparison.OrdinalIgnoreCase) && IsInteger(c.TypeName));
        var month = columns.FirstOrDefault(c =>
            c.Name.Equals("Month", StringComparison.OrdinalIgnoreCase) && IsInteger(c.TypeName));

        if (year is not null && month is not null)
        {
            return $"MAX(DATEFROMPARTS({SqlIdentifier.QuoteColumn(year.Name)}, " +
                   $"{SqlIdentifier.QuoteColumn(month.Name)}, 1))";
        }

        var dateColumn = columns.FirstOrDefault(c =>
            IsDate(c.TypeName) && !_watermarkIgnoreColumns.Contains(c.Name));

        if (dateColumn is not null)
        {
            return $"MAX({SqlIdentifier.QuoteColumn(dateColumn.Name)})";
        }

        throw new InvalidInsertRequestException(
            $"{quotedTarget} has neither a Year + Month pair of integer columns nor a date column, so " +
            "there is no way to tell how far it has been loaded" +
            (_watermarkIgnoreColumns.Count == 0
                ? ". "
                : $" (ignoring {string.Join(", ", _watermarkIgnoreColumns)}, which record when this API " +
                  "wrote the row). ") +
            $"Add an entry under 'Api:WatermarkExpressions:{key}' in this API's appsettings.json.");
    }

    private async Task<IList<ColumnInfo>> LoadAllColumnsAsync(
        SqlConnection connection,
        string quotedTarget,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT c.name, t.name AS type_name " +
            "FROM sys.columns AS c " +
            "JOIN sys.types AS t " +
            "  ON t.system_type_id = c.system_type_id AND t.user_type_id = t.system_type_id " +
            "WHERE c.object_id = OBJECT_ID(@target) " +
            "ORDER BY c.column_id;";

        var columns = new List<ColumnInfo>();

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.Add("@target", SqlDbType.NVarChar, 512).Value = quotedTarget;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new ColumnInfo(reader.GetString(0), reader.GetString(1)));
        }

        return columns;
    }

    private static bool IsInteger(string sqlTypeName)
    {
        return sqlTypeName.ToLowerInvariant() is "tinyint" or "smallint" or "int" or "bigint";
    }

    private static bool IsDate(string sqlTypeName)
    {
        return sqlTypeName.ToLowerInvariant()
            is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset";
    }

    private sealed record ColumnInfo(string Name, string TypeName);

    public async Task<TableInsertResponse> InsertAsync(
        TableInsertRequest request,
        CancellationToken cancellationToken)
    {
        var target = SqlIdentifier.QuoteName(request.TableName, "tableName");

        ValidateShape(request);

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await EnsureInsertableObjectAsync(connection, target, cancellationToken);

        var targetColumns = await LoadTargetColumnsAsync(connection, target, cancellationToken);
        var insertColumns = ResolveInsertColumns(request.Columns, targetColumns, target, out var matching);

        // A pre-load clear forces the transaction on whatever the caller asked for: the one
        // outcome that must not be possible is an emptied target that never gets refilled.
        var clearing = request.PreLoad is not null && request.PreLoad.Action != PreLoadAction.None;
        var transaction = request.UseTransaction || clearing
            ? (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        try
        {
            var deleted = await ApplyPreLoadAsync(
                connection, transaction, target, request.PreLoad, cancellationToken);

            var inserted = await InsertRowsAsync(
                connection, transaction, target, insertColumns, request.Rows, cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            _logger.LogInformation(
                "{Target}: {Matching} Inserted {Inserted} row(s), pre-load removed {Deleted}.",
                target, matching, inserted, deleted);

            return new TableInsertResponse
            {
                TableName = request.TableName,
                RowsInserted = inserted,
                RowsDeleted = deleted,
                ColumnMatching = matching
            };
        }
        catch
        {
            await SafeRollbackAsync(transaction);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static void ValidateShape(TableInsertRequest request)
    {
        var width = request.Columns.Count;

        for (var i = 0; i < request.Rows.Count; i++)
        {
            var row = request.Rows[i];
            if (row is null)
            {
                throw new InvalidInsertRequestException($"Row {i + 1} is null.");
            }

            if (row.Count != width)
            {
                throw new InvalidInsertRequestException(
                    $"Row {i + 1} has {row.Count} value(s) but {width} column(s) were declared.");
            }
        }
    }

    /// <summary>Separates "no such table" from "that name is not something you can insert into".</summary>
    private async Task EnsureInsertableObjectAsync(
        SqlConnection connection,
        string quotedTarget,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT o.type_desc FROM sys.objects AS o WHERE o.object_id = OBJECT_ID(@target);";

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = _commandTimeoutSeconds;
        command.Parameters.Add("@target", SqlDbType.NVarChar, 512).Value = quotedTarget;

        var typeDescription = await command.ExecuteScalarAsync(cancellationToken) as string;

        if (typeDescription is null)
        {
            throw new TableNotFoundException(
                $"Table {quotedTarget} was not found, or this API's account cannot see it.");
        }

        if (typeDescription != "USER_TABLE" && typeDescription != "VIEW")
        {
            throw new InvalidInsertRequestException(
                $"{quotedTarget} is a {typeDescription}, not a table or view.");
        }
    }

    /// <summary>
    /// Reads the structure of the target. Only columns that can actually be inserted into are
    /// returned, in the table's own order — identity, computed and rowversion columns are left
    /// for SQL Server to fill, so a target with an identity key needs no special handling.
    /// </summary>
    private async Task<IList<TargetColumn>> LoadTargetColumnsAsync(
        SqlConnection connection,
        string quotedTarget,
        CancellationToken cancellationToken)
    {
        const string sql =
            "SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale " +
            "FROM sys.columns AS c " +
            "JOIN sys.types AS t " +
            "  ON t.system_type_id = c.system_type_id AND t.user_type_id = t.system_type_id " +
            "WHERE c.object_id = OBJECT_ID(@target) " +
            "  AND c.is_identity = 0 " +
            "  AND c.is_computed = 0 " +
            "  AND c.system_type_id <> 189 " +   // rowversion / timestamp
            "ORDER BY c.column_id;";

        var columns = new List<TargetColumn>();

        using (var command = new SqlCommand(sql, connection))
        {
            command.CommandTimeout = _commandTimeoutSeconds;
            command.Parameters.Add("@target", SqlDbType.NVarChar, 512).Value = quotedTarget;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new TargetColumn(
                    reader.GetString(0),
                    MapType(reader.GetString(1)),
                    reader.GetInt16(2),
                    reader.GetByte(3),
                    reader.GetByte(4)));
            }
        }

        if (columns.Count == 0)
        {
            throw new InvalidInsertRequestException(
                $"Target table {quotedTarget} has no insertable columns.");
        }

        return columns;
    }

    /// <summary>
    /// Decides which target columns the INSERT lists, and in which order, so the caller's values
    /// land in the right places. Names win when they all match; otherwise the target's own column
    /// order is used and the two must be the same width.
    /// </summary>
    private static IList<TargetColumn> ResolveInsertColumns(
        IList<string?> queryColumns,
        IList<TargetColumn> targetColumns,
        string quotedTarget,
        out string matching)
    {
        var canonical = new Dictionary<string, TargetColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in targetColumns)
        {
            canonical[column.Name] = column;
        }

        var allNamed = queryColumns.All(name => !string.IsNullOrWhiteSpace(name));
        var noDuplicates = queryColumns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count() == queryColumns.Count;
        var allKnown = allNamed && queryColumns.All(name => canonical.ContainsKey(name!));

        if (allNamed && noDuplicates && allKnown)
        {
            matching = $"{quotedTarget} columns matched by name.";
            return queryColumns.Select(name => canonical[name!]).ToList();
        }

        if (queryColumns.Count == targetColumns.Count)
        {
            // Positional: the caller's Nth value goes into the target's Nth insertable column.
            // Reported in full, because a silent mis-pairing would corrupt data.
            var pairs = targetColumns
                .Select((column, index) => $"[{column.Name}] <- {Describe(queryColumns[index], index)}");

            matching = $"{quotedTarget} columns matched by position: {string.Join(", ", pairs)}.";
            return targetColumns;
        }

        throw new InvalidInsertRequestException(
            $"The request declares {queryColumns.Count} column(s) ({DescribeAll(queryColumns)}), which do " +
            $"not fit {quotedTarget}. Its {targetColumns.Count} insertable column(s) are: " +
            $"{string.Join(", ", targetColumns.Select(column => column.Name))}. Either name the columns to " +
            "match the target's, or send them in the target's column order.");
    }

    /// <returns>Rows removed. TRUNCATE reports none, so it always returns 0.</returns>
    private async Task<int> ApplyPreLoadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string quotedTarget,
        PreLoadRequest? preLoad,
        CancellationToken cancellationToken)
    {
        if (preLoad is null || preLoad.Action == PreLoadAction.None)
        {
            return 0;
        }

        var predicate = preLoad.Where?.Trim() ?? string.Empty;

        if (preLoad.Action == PreLoadAction.Truncate && predicate.Length > 0)
        {
            throw new InvalidInsertRequestException(
                "preLoad.where cannot be combined with action 'Truncate', because TRUNCATE always " +
                "removes every row. Use action 'Delete' for a filtered refresh.");
        }

        var sql = preLoad.Action == PreLoadAction.Truncate
            ? $"TRUNCATE TABLE {quotedTarget};"
            : predicate.Length == 0
                ? $"DELETE FROM {quotedTarget};"
                : $"DELETE FROM {quotedTarget} WHERE {predicate};";

        using var command = new SqlCommand(sql, connection, transaction);
        command.CommandTimeout = _commandTimeoutSeconds;

        // Only the parameters the predicate actually mentions are bound, so an unused one in
        // the request is harmless rather than an error from SQL Server.
        foreach (var parameter in preLoad.Parameters ?? new Dictionary<string, DateTime>())
        {
            if (!ParameterNamePattern.IsMatch(parameter.Key))
            {
                throw new InvalidInsertRequestException(
                    $"preLoad.parameters has key '{parameter.Key}'. It must look like '@FromDate'.");
            }

            if (predicate.IndexOf(parameter.Key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                command.Parameters.Add(parameter.Key, SqlDbType.DateTime).Value = parameter.Value;
            }
        }

        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        return removed < 0 ? 0 : removed;
    }

    /// <summary>
    /// Writes the rows as parameterized INSERT ... VALUES batches. Values are never inlined into
    /// the statement text; only the validated column names are.
    /// </summary>
    private async Task<int> InsertRowsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string quotedTarget,
        IList<TargetColumn> columns,
        List<List<JsonElement>> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var columnList = string.Join(", ", columns.Select(column => SqlIdentifier.QuoteColumn(column.Name)));
        var rowsPerBatch = Math.Max(1, Math.Min(MaxRowsPerBatch, MaxParametersPerBatch / columns.Count));

        var inserted = 0;

        for (var start = 0; start < rows.Count; start += rowsPerBatch)
        {
            var count = Math.Min(rowsPerBatch, rows.Count - start);

            var sql = new StringBuilder();
            sql.Append("INSERT INTO ").Append(quotedTarget).Append(" (").Append(columnList).AppendLine(")");
            sql.Append("VALUES ");

            using var command = new SqlCommand { Connection = connection, Transaction = transaction };
            command.CommandTimeout = _commandTimeoutSeconds;

            for (var r = 0; r < count; r++)
            {
                if (r > 0)
                {
                    sql.Append(", ");
                }

                sql.Append('(');

                for (var c = 0; c < columns.Count; c++)
                {
                    var name = "@p" + r.ToString(CultureInfo.InvariantCulture)
                                    + "_" + c.ToString(CultureInfo.InvariantCulture);

                    if (c > 0)
                    {
                        sql.Append(", ");
                    }

                    sql.Append(name);

                    command.Parameters.Add(CreateParameter(
                        name, columns[c], rows[start + r][c], start + r + 1));
                }

                sql.Append(')');
            }

            sql.Append(';');
            command.CommandText = sql.ToString();

            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            inserted += affected < 0 ? 0 : affected;
        }

        return inserted;
    }

    private static SqlParameter CreateParameter(
        string name,
        TargetColumn column,
        JsonElement value,
        int rowNumber)
    {
        var parameter = new SqlParameter(name, column.DbType)
        {
            Value = ToParameterValue(value, column, rowNumber)
        };

        switch (column.DbType)
        {
            case SqlDbType.NChar:
            case SqlDbType.NVarChar:
                // max_length is bytes for the unicode types, and -1 means MAX.
                parameter.Size = column.MaxLengthBytes < 0 ? -1 : column.MaxLengthBytes / 2;
                break;

            case SqlDbType.Char:
            case SqlDbType.VarChar:
            case SqlDbType.Binary:
            case SqlDbType.VarBinary:
                parameter.Size = column.MaxLengthBytes < 0 ? -1 : column.MaxLengthBytes;
                break;

            case SqlDbType.Decimal:
                parameter.Precision = column.Precision;
                parameter.Scale = column.Scale;
                break;

            case SqlDbType.Time:
            case SqlDbType.DateTime2:
            case SqlDbType.DateTimeOffset:
                parameter.Scale = column.Scale;
                break;
        }

        return parameter;
    }

    /// <summary>
    /// Converts one JSON value to the CLR type the target column expects, so a date arrives as a
    /// date and a decimal keeps its precision instead of going through a double.
    /// </summary>
    private static object ToParameterValue(JsonElement value, TargetColumn column, int rowNumber)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return DBNull.Value;
        }

        try
        {
            switch (column.DbType)
            {
                case SqlDbType.BigInt:
                    return AsInt64(value);
                case SqlDbType.Int:
                    return (int)AsInt64(value);
                case SqlDbType.SmallInt:
                    return (short)AsInt64(value);
                case SqlDbType.TinyInt:
                    return (byte)AsInt64(value);

                case SqlDbType.Bit:
                    return AsBoolean(value);

                case SqlDbType.Decimal:
                case SqlDbType.Money:
                case SqlDbType.SmallMoney:
                    return AsDecimal(value);

                case SqlDbType.Float:
                    return AsDouble(value);
                case SqlDbType.Real:
                    return (float)AsDouble(value);

                case SqlDbType.Date:
                case SqlDbType.DateTime:
                case SqlDbType.DateTime2:
                case SqlDbType.SmallDateTime:
                    return DateTime.Parse(
                        AsString(value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                case SqlDbType.DateTimeOffset:
                    return DateTimeOffset.Parse(
                        AsString(value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                case SqlDbType.Time:
                    return TimeSpan.Parse(AsString(value), CultureInfo.InvariantCulture);

                case SqlDbType.UniqueIdentifier:
                    return Guid.Parse(AsString(value));

                case SqlDbType.Binary:
                case SqlDbType.VarBinary:
                case SqlDbType.Image:
                    return Convert.FromBase64String(AsString(value));

                default:
                    return AsString(value);
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            throw new InvalidInsertRequestException(
                $"Row {rowNumber}, column [{column.Name}]: {value.GetRawText()} is not a valid " +
                $"{column.DbType} value. {ex.Message}");
        }
    }

    private static long AsInt64(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetInt64(),
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            _ => long.Parse(AsString(value), CultureInfo.InvariantCulture)
        };
    }

    private static bool AsBoolean(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetDecimal() != 0m,
            _ => AsString(value) switch
            {
                "1" => true,
                "0" => false,
                var text => bool.Parse(text)
            }
        };
    }

    private static decimal AsDecimal(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.True => 1m,
            JsonValueKind.False => 0m,
            _ => decimal.Parse(AsString(value), NumberStyles.Float, CultureInfo.InvariantCulture)
        };
    }

    private static double AsDouble(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.True => 1d,
            JsonValueKind.False => 0d,
            _ => double.Parse(AsString(value), NumberStyles.Float, CultureInfo.InvariantCulture)
        };
    }

    private static string AsString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => value.GetRawText()
        };
    }

    private static SqlDbType MapType(string sqlTypeName)
    {
        return sqlTypeName.ToLowerInvariant() switch
        {
            "bigint" => SqlDbType.BigInt,
            "int" => SqlDbType.Int,
            "smallint" => SqlDbType.SmallInt,
            "tinyint" => SqlDbType.TinyInt,
            "bit" => SqlDbType.Bit,
            "decimal" or "numeric" => SqlDbType.Decimal,
            "money" => SqlDbType.Money,
            "smallmoney" => SqlDbType.SmallMoney,
            "float" => SqlDbType.Float,
            "real" => SqlDbType.Real,
            "date" => SqlDbType.Date,
            "datetime" => SqlDbType.DateTime,
            "datetime2" => SqlDbType.DateTime2,
            "smalldatetime" => SqlDbType.SmallDateTime,
            "datetimeoffset" => SqlDbType.DateTimeOffset,
            "time" => SqlDbType.Time,
            "char" => SqlDbType.Char,
            "nchar" => SqlDbType.NChar,
            "varchar" => SqlDbType.VarChar,
            "nvarchar" or "sysname" => SqlDbType.NVarChar,
            "text" => SqlDbType.Text,
            "ntext" => SqlDbType.NText,
            "binary" => SqlDbType.Binary,
            "varbinary" => SqlDbType.VarBinary,
            "image" => SqlDbType.Image,
            "uniqueidentifier" => SqlDbType.UniqueIdentifier,
            "xml" => SqlDbType.Xml,

            // Anything exotic (geography, hierarchyid, a CLR type) is sent as text and left for
            // SQL Server to convert, which is what it does for a literal too.
            _ => SqlDbType.NVarChar
        };
    }

    private static string Describe(string? columnName, int index)
    {
        return string.IsNullOrWhiteSpace(columnName)
            ? $"column {index + 1}"
            : $"'{columnName}'";
    }

    private static string DescribeAll(IEnumerable<string?> columnNames)
    {
        return string.Join(", ", columnNames.Select(Describe));
    }

    private static async Task SafeRollbackAsync(SqlTransaction? transaction)
    {
        if (transaction?.Connection is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync();
        }
        catch
        {
            // The transaction may already be doomed by the server; the original error wins.
        }
    }

    /// <summary>One insertable column of the target, with what it takes to bind a value to it.</summary>
    private sealed record TargetColumn(
        string Name,
        SqlDbType DbType,
        short MaxLengthBytes,
        byte Precision,
        byte Scale);
}