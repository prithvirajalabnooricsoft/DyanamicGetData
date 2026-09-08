using DynamicTableApi.Data;
using DynamicTableApi.Models;
using DynamicTableApi.Security;
using DynamicTableApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace DynamicTableApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[ApiKey]
public class DataController : ControllerBase
{
    private readonly IDynamicTableService _service;
    private readonly ILogger<DataController> _logger;

    public DataController(IDynamicTableService service, ILogger<DataController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// Returns the newest date the given table already holds.
    /// </summary>
    /// <remarks>
    /// The caller runs on a different server and cannot see this table, so it asks here before
    /// running its query and resumes from whatever comes back. How the date is found is decided on
    /// this side, from the table's own columns — a Year + Month pair of integer columns, otherwise
    /// its first date column. Requires the <c>X-Api-Key</c> header.
    ///
    /// A null <c>watermark</c> means the table holds no dated rows yet, which is what makes a first
    /// run seed itself rather than fail.
    /// </remarks>
    /// <param name="tableName">Target table, one to three parts.</param>
    /// <response code="200">The newest date, and the expression used to find it.</response>
    /// <response code="400">No column on that table can date its rows.</response>
    /// <response code="401">The X-Api-Key header is missing or wrong.</response>
    /// <response code="404">The table does not exist.</response>
    [HttpGet("watermark")]
    [ProducesResponseType(typeof(WatermarkResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Watermark(
        [FromQuery] string tableName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return BadRequest("tableName is required.");
        }

        try
        {
            return Ok(await _service.GetWatermarkAsync(tableName, cancellationToken));
        }
        catch (TableNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidInsertRequestException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (SqlException ex)
        {
            _logger.LogError(ex, "Watermark read on {TableName} failed.", tableName);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                $"SQL error {ex.Number} (line {ex.LineNumber}): {ex.Message}");
        }
    }

    /// <summary>
    /// Inserts a batch of rows into the given table, optionally clearing it first.
    /// </summary>
    /// <remarks>
    /// The caller sends data, never SQL: the INSERT and its column list are generated here from
    /// the target table's own structure. Requires the <c>X-Api-Key</c> header.
    ///
    /// Sample request:
    ///
    ///     POST /api/data
    ///     X-Api-Key: &lt;your key&gt;
    ///     {
    ///       "tableName": "ELRC_MonthlyPLSummary",
    ///       "columns": ["Year", "Month", "Amount"],
    ///       "rows": [[2026, 8, 14500.00], [2026, 7, 9200.50]],
    ///       "preLoad": {
    ///         "action": "Delete",
    ///         "where": "[Year] = YEAR(@FromDate) AND [Month] &gt;= MONTH(@FromDate)",
    ///         "parameters": { "@FromDate": "2026-07-01T00:00:00" }
    ///       }
    ///     }
    /// </remarks>
    /// <response code="200">The rows were inserted; the body reports how many.</response>
    /// <response code="400">The request cannot be turned into an INSERT.</response>
    /// <response code="401">The X-Api-Key header is missing or wrong.</response>
    /// <response code="404">The target table does not exist.</response>
    [HttpPost]
    [ProducesResponseType(typeof(TableInsertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Insert(
        [FromBody] TableInsertRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest("A request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.TableName))
        {
            return BadRequest("tableName is required.");
        }

        if (request.Columns is null || request.Columns.Count == 0)
        {
            return BadRequest("columns is required and must list at least one column.");
        }

        if (request.Rows is null)
        {
            return BadRequest("rows is required; send an empty array to run only the pre-load.");
        }

        try
        {
            var response = await _service.InsertAsync(request, cancellationToken);
            return Ok(response);
        }
        catch (TableNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidInsertRequestException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (SqlException ex)
        {
            // The caller writes this text into its own status column, so the server's reason has
            // to travel back rather than being swallowed into a bare 500.
            _logger.LogError(ex, "Insert into {TableName} failed.", request.TableName);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                $"SQL error {ex.Number} (line {ex.LineNumber}): {ex.Message}");
        }
    }
}