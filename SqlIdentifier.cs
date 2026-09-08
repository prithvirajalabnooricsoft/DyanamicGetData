using System.Linq;
using System.Text.RegularExpressions;

namespace DynamicTableApi.Data;

/// <summary>
/// Validates and bracket-quotes SQL object names. Table and column names arrive from the
/// caller's request body, so they are checked against this whitelist before ever being
/// concatenated into a statement — parameters only work for values, not identifiers.
/// </summary>
internal static class SqlIdentifier
{
    private static readonly Regex PartPattern =
        new(@"^[A-Za-z_][A-Za-z0-9_$#@ ]*$", RegexOptions.Compiled);

    /// <summary>
    /// Quotes a one to three part name: Sales -&gt; [Sales];
    /// dbo.Sales -&gt; [dbo].[Sales]; Db.dbo.Sales -&gt; [Db].[dbo].[Sales].
    /// </summary>
    public static string QuoteName(string? name, string description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidInsertRequestException($"{description} is empty.");
        }

        var parts = name.Trim().Split('.');
        if (parts.Length > 3)
        {
            throw new InvalidInsertRequestException(
                $"{description} has value '{name}', which has more than three parts.");
        }

        foreach (var part in parts)
        {
            if (!PartPattern.IsMatch(Unquote(part)))
            {
                throw new InvalidInsertRequestException(
                    $"{description} has value '{name}', which is not a valid SQL object name.");
            }
        }

        return string.Join(".", parts.Select(part => "[" + Unquote(part) + "]"));
    }

    /// <summary>Quotes a single column name.</summary>
    public static string QuoteColumn(string? name)
    {
        var bare = Unquote(name);
        if (!PartPattern.IsMatch(bare))
        {
            throw new InvalidInsertRequestException($"'{name}' is not a valid SQL column name.");
        }

        return "[" + bare + "]";
    }

    public static string Unquote(string? identifier)
    {
        return identifier == null ? string.Empty : identifier.Trim().Trim('[', ']').Trim();
    }
}

/// <summary>The request itself is wrong — reported to the caller as 400, not 500.</summary>
public sealed class InvalidInsertRequestException : Exception
{
    public InvalidInsertRequestException(string message) : base(message)
    {
    }
}

/// <summary>The table named in the request does not exist — reported as 404.</summary>
public sealed class TableNotFoundException : Exception
{
    public TableNotFoundException(string message) : base(message)
    {
    }
}