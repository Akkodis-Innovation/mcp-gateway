// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Tools.Contracts;
using Microsoft.McpGateway.Tools.Services;
using MongoDB.Driver;
using System.Linq;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add logging
builder.Services.AddLogging();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPermissionProvider, SimplePermissionProvider>();
// Add HttpClient for tool execution
builder.Services.AddHttpClient();

var mongoConfig = builder.Configuration.GetSection("MongoSettings");
var mongoConnectionString = mongoConfig["ConnectionString"] ?? "mongodb://localhost:27017";
var mongoDatabaseName = mongoConfig["DatabaseName"] ?? "McpGatewayDb";
var toolCollectionName = mongoConfig["ToolCollectionName"] ?? "tools";

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDatabaseName));

builder.Services.AddSingleton<IToolResourceStore>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MongoToolResourceStore>>();
    return new MongoToolResourceStore(sp.GetRequiredService<IMongoDatabase>(), toolCollectionName, logger);
});

if (builder.Environment.IsDevelopment())
{
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}

// Register IToolDefinitionProvider using the store
builder.Services.AddSingleton<IToolDefinitionProvider, StorageToolDefinitionProvider>();

// Register tool executor
builder.Services.AddSingleton<IToolExecutor, HttpToolExecutor>();

builder.Services.AddMcpServer()
    .WithListToolsHandler(static (c, ct) =>
    {
        var toolDefinitionProvider = c.Services!.GetRequiredService<IToolDefinitionProvider>();
        return toolDefinitionProvider?.ListToolsAsync(c, ct) ?? throw new InvalidOperationException("Tool registry not properly registered.");
    })
    .WithCallToolHandler(static (c, ct) =>
    {
        var toolExecutor = c.Services!.GetRequiredService<IToolExecutor>();
        return toolExecutor?.ExecuteToolAsync(c, ct) ?? throw new InvalidOperationException("Tool executor not properly registered.");
    })
    .WithHttpTransport();


builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8000);
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue(ForwardedIdentityHeaders.UserId, out var forwardedUserId) && !string.IsNullOrWhiteSpace(forwardedUserId))
    {
        var userId = forwardedUserId.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var identity = new ClaimsIdentity("Forwarded");
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId));

            if (context.Request.Headers.TryGetValue(ForwardedIdentityHeaders.Roles, out var forwardedRoles))
            {
                var roles = forwardedRoles.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var role in roles.Where(role => !string.IsNullOrWhiteSpace(role)))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }
            }

            context.User = new ClaimsPrincipal(identity);
        }
    }

    await next().ConfigureAwait(false);
});

if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (!context.User?.Identities?.Any(identity => identity.IsAuthenticated) ?? true)
        {
            var devIdentity = new ClaimsIdentity("Development");
            devIdentity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "dev"));
            devIdentity.AddClaim(new Claim(ClaimTypes.Name, "dev"));
            devIdentity.AddClaim(new Claim(ClaimTypes.Role, "mcp.dev"));
            context.User = new ClaimsPrincipal(devIdentity);
        }

        await next().ConfigureAwait(false);
    });
}
app.MapMcp();
await app.RunAsync();
