using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DynamicTableApi.Security;

/// <summary>
/// Requires a shared secret in the <see cref="HeaderName"/> request header, matched against
/// the "Api:Key" configuration value. This endpoint writes to arbitrary tables, so it is
/// never reachable without one — a missing configured key fails the request rather than
/// waving it through.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ApiKeyAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Api-Key";

    /// <summary>Configuration key holding the expected value.</summary>
    public const string ConfigurationKey = "Api:Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = configuration[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(expected))
        {
            // Program.cs refuses to start without a key, so this only fires if configuration
            // was reloaded to an empty value. Fail closed either way.
            context.Result = new ObjectResult($"'{ConfigurationKey}' is not configured on the server.")
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var provided) ||
            !Matches(expected, provided.ToString()))
        {
            context.Result = new UnauthorizedObjectResult(
                $"A valid '{HeaderName}' header is required.");
            return;
        }

        await next();
    }

    /// <summary>Length-independent comparison, so a wrong key leaks no timing information.</summary>
    private static bool Matches(string expected, string? provided)
    {
        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }
}