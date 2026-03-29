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

var redisConnection = builder.Configuration.GetValue<string>("Redis:ConnectionString") ?? "localhost:6379";
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = "mcpgateway:";
});

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
            Resource = new Uri(builder.Configuration.GetValue<string>("PublicOrigin")!),
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

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
await app.RunAsync();
