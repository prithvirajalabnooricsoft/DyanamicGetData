using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicTableApi.Models;

/// <summary>
/// A batch of rows to insert into one table. The caller (the AIIcsoftMetaData service) has
/// already run its query; this API owns everything from here on — working out the INSERT's
/// column list, clearing the target and writing the rows.
/// </summary>
public class TableInsertRequest
{
    /// <summary>Target table, one to three parts, e.g. "ELRC_MonthlyPLSummary" or "dbo.Sales".</summary>
    public string TableName { get; set; } = string.Empty;

    /// <summary>
    /// The column names the caller's query returned, in the order the values appear in each
    /// row. Entries may be null or blank for unaliased columns — those fall back to positional
    /// matching against the target table.
    /// </summary>
    public List<string?> Columns { get; set; } = new();

    /// <summary>
    /// The rows, each an array of values in the same order as <see cref="Columns"/>. May be
    /// empty, which runs the pre-load and inserts nothing.
    /// </summary>
    public List<List<JsonElement>> Rows { get; set; } = new();

    /// <summary>What to clear from the target first. Omit to append.</summary>
    public PreLoadRequest? PreLoad { get; set; }

    /// <summary>
    /// Wrap the work in a transaction. A pre-load clear always forces one on regardless, so
    /// clearing a target and then failing to refill it is not a possible outcome.
    /// </summary>
    public bool UseTransaction { get; set; } = true;
}

/// <summary>What to remove from the target table before inserting.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreLoadAction
{
    /// <summary>Insert only; existing rows are left alone.</summary>
    None = 0,

    /// <summary>DELETE the target rows first, optionally narrowed by <see cref="PreLoadRequest.Where"/>.</summary>
    Delete = 1,

    /// <summary>TRUNCATE TABLE the target. Ignores <see cref="PreLoadRequest.Where"/>.</summary>
    Truncate = 2
}

public class PreLoadRequest
{
    public PreLoadAction Action { get; set; } = PreLoadAction.None;

    /// <summary>
    /// Optional predicate for <see cref="PreLoadAction.Delete"/>, without the WHERE keyword,
    /// e.g. "[Year] = YEAR(@FromDate)". Empty deletes every row. It may reference any of the
    /// names in <see cref="Parameters"/>; only the ones it actually mentions are bound.
    /// </summary>
    public string? Where { get; set; }

    /// <summary>
    /// Date values the predicate may use, keyed by parameter name including the '@',
    /// e.g. {"@FromDate": "2026-08-01T00:00:00"}.
    /// </summary>
    public Dictionary<string, DateTime>? Parameters { get; set; }
}

/// <summary>
/// The newest date a target already holds. The caller cannot see the target - it lives on this
/// server - so it asks for this before running its query, and continues from where it left off.
/// </summary>
public class WatermarkResponse
{
    public string TableName { get; set; } = string.Empty;

    /// <summary>Null when the target holds no dated rows yet, which seeds a first run.</summary>
    public DateTime? Watermark { get; set; }

    /// <summary>The expression used, so the caller can log what it was told and why.</summary>
    public string Expression { get; set; } = string.Empty;
}

/// <summary>What the insert did, so the caller can log it and stamp its own status row.</summary>
public class TableInsertResponse
{
    public string TableName { get; set; } = string.Empty;

    public int RowsInserted { get; set; }

    /// <summary>Rows removed by the pre-load clear. Zero when no pre-load was requested.</summary>
    public int RowsDeleted { get; set; }

    /// <summary>
    /// How the caller's columns were paired with the target's, in the same wording the service
    /// used to log itself — "columns matched by name", or the full positional pairing.
    /// </summary>
    public string ColumnMatching { get; set; } = string.Empty;
}