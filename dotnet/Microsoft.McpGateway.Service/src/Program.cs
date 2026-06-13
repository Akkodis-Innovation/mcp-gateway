// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.McpGateway.Management.Authorization;
using Microsoft.McpGateway.Management.Deployment;
using Microsoft.McpGateway.Management.Service;
using Microsoft.McpGateway.Management.Store;
using Microsoft.McpGateway.Service.Authentication;
using Microsoft.McpGateway.Service.Routing;
using Microsoft.McpGateway.Service.Session;
using ModelContextProtocol.AspNetCore.Authentication;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddLogging();

builder.Services.AddSingleton<IKubernetesClientFactory, LocalKubernetesClientFactory>();
builder.Services.AddSingleton<IAdapterSessionStore, DistributedMemorySessionStore>();
builder.Services.AddSingleton<IServiceNodeInfoProvider, AdapterKubernetesNodeInfoProvider>();
builder.Services.AddSingleton<ISessionRoutingHandler, AdapterSessionRoutingHandler>();

builder.Services.AddDistributedMemoryCache();

var mongoConfig = builder.Configuration.GetSection("MongoSettings");
var mongoConnectionString = mongoConfig["ConnectionString"] ?? "mongodb://localhost:27017";
var mongoDatabaseName = mongoConfig["DatabaseName"] ?? "McpGatewayDb";
var adapterCollectionName = mongoConfig["AdapterCollectionName"] ?? "adapters";
var toolCollectionName = mongoConfig["ToolCollectionName"] ?? "tools";

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(mongoDatabaseName));

builder.Services.AddSingleton<IAdapterResourceStore>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MongoAdapterResourceStore>>();
    return new MongoAdapterResourceStore(sp.GetRequiredService<IMongoDatabase>(), adapterCollectionName, logger);
});

builder.Services.AddSingleton<IToolResourceStore>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<MongoToolResourceStore>>();
    return new MongoToolResourceStore(sp.GetRequiredService<IMongoDatabase>(), toolCollectionName, logger);
});

if (builder.Environment.IsDevelopment())
{
    builder.Services
        .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);

    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
}
else
{
    var azureAdConfig = builder.Configuration.GetSection("AzureAd");
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddScheme<McpAuthenticationOptions, McpSubPathAwareAuthenticationHandler>(
        McpAuthenticationDefaults.AuthenticationScheme,
        McpAuthenticationDefaults.DisplayName,
    options =>
    {
        options.ResourceMetadata = new()
        {
            Resource = new Uri(azureAdConfig["Audience"]!),
            AuthorizationServers = { new Uri($"https://login.microsoftonline.com/{azureAdConfig["TenantId"]}/v2.0") },
            ScopesSupported = [$"api://{azureAdConfig["ClientId"]}/.default"]
        };
    })
    .AddMicrosoftIdentityWebApi(azureAdConfig);
}

builder.Services.AddSingleton<IKubeClientWrapper>(c =>
{
    var kubeClientFactory = c.GetRequiredService<IKubernetesClientFactory>();
    return new KubeClient(kubeClientFactory, "adapter");
});
builder.Services.AddSingleton<IPermissionProvider, SimplePermissionProvider>();
builder.Services.AddSingleton<IAdapterDeploymentManager>(c =>
{
    var config = builder.Configuration.GetSection("ContainerRegistrySettings");
    return new KubernetesAdapterDeploymentManager(config["Endpoint"]!, c.GetRequiredService<IKubeClientWrapper>(), c.GetRequiredService<ILogger<KubernetesAdapterDeploymentManager>>());
});
builder.Services.AddSingleton<IAdapterManagementService, AdapterManagementService>();
builder.Services.AddSingleton<IToolManagementService, ToolManagementService>();
builder.Services.AddSingleton<IAdapterRichResultProvider, AdapterRichResultProvider>();

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddHttpClient();

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8000);
});

var app = builder.Build();

// OAuth proxy endpoints so VS Code can use the gateway URL as the authorization server.
var tenantId = app.Configuration["AzureAd:TenantId"]!;
var clientId = app.Configuration["AzureAd:ClientId"]!;
var publicOrigin = app.Configuration["PublicOrigin"]!;

// OIDC discovery — VS Code fetches this to find authorize + token endpoints
app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
{
    issuer = publicOrigin,
    authorization_endpoint = $"{publicOrigin}/authorize",
    token_endpoint = $"{publicOrigin}/token",
    response_types_supported = new[] { "code" },
    code_challenge_methods_supported = new[] { "S256" },
    grant_types_supported = new[] { "authorization_code" },
})).AllowAnonymous();

app.MapGet("/authorize", (HttpRequest request) =>
{
    var qs = request.QueryString.Value ?? "";
    return Results.Redirect(
        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize{qs}",
        permanent: false);
}).AllowAnonymous();

app.MapPost("/token", async (HttpRequest request, IHttpClientFactory clientFactory) =>
{
    var form = await request.ReadFormAsync();
    var params_ = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
    // Inject scope if missing — Entra v2 requires it
    if (!params_.ContainsKey("scope") || string.IsNullOrEmpty(params_["scope"]))
        params_["scope"] = $"api://{clientId}/.default";
    var client = clientFactory.CreateClient();
    var resp = await client.PostAsync(
        $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
        new FormUrlEncodedContent(params_));
    var body = await resp.Content.ReadAsStringAsync();
    return Results.Content(body, "application/json", statusCode: (int)resp.StatusCode);
}).AllowAnonymous();

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.RunAsync();
