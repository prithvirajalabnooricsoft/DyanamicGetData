using DynamicTableApi.Security;
using DynamicTableApi.Services;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// This endpoint writes to arbitrary tables, so refuse to start without a key rather than
// leaving a window where it is reachable unauthenticated.
if (string.IsNullOrWhiteSpace(builder.Configuration[ApiKeyAttribute.ConfigurationKey]))
{
    throw new InvalidOperationException(
        $"'{ApiKeyAttribute.ConfigurationKey}' is not configured. Set it in appsettings.json, or as the " +
        "environment variable Api__Key, before starting the API.");
}

// Result sets can be large, and the whole batch arrives in one body.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize =
        builder.Configuration.GetValue<long>("Api:MaxRequestBodyBytes", 268_435_456);
});

// Controllers
builder.Services.AddControllers();

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Dynamic Table API",
        Version = "v1",
        Description = "Post rows and a table name; the INSERT is generated from that table's structure."
    });

    // Puts the "Authorize" button in Swagger UI so the key can be sent from there.
    c.AddSecurityDefinition(ApiKeyAttribute.HeaderName, new OpenApiSecurityScheme
    {
        Name = ApiKeyAttribute.HeaderName,
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "Shared secret configured as Api:Key on the server."
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = ApiKeyAttribute.HeaderName
                }
            },
            Array.Empty<string>()
        }
    });
});

// Register our data service (reads connection string from appsettings.json)
builder.Services.AddScoped<IDynamicTableService, DynamicTableService>();

var app = builder.Build();

// Always enable Swagger UI here (remove the IsDevelopment check if you want it in prod too)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Dynamic Table API v1");
});

// No UseHttpsRedirection: this API's only client is the AIIcsoftMetaData service, and a 307 to
// the https endpoint just moves a POST onto a certificate the calling machine has to trust -
// which fails against the ASP.NET developer certificate. Redirecting only ever helps a browser.
// To require TLS, bind https alone (see the Kestrel section of appsettings.json) rather than
// redirecting to it, and point the service's ApiBaseUrl at that address.
app.UseAuthorization();
app.MapControllers();

app.Run();