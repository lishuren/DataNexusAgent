using Azure;
using Azure.AI.Inference;
using DataNexus.Agents;
using DataNexus.Agents.Af;
using DataNexus.Core;
using DataNexus.Endpoints;
using DataNexus.Identity;
using DataNexus.Plugins;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Authentication — Keycloak (OpenID Connect / JWT Bearer)
// ---------------------------------------------------------------------------
var keycloakSection = builder.Configuration.GetSection("Keycloak");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = keycloakSection["Authority"];
        options.Audience = keycloakSection["Audience"];
        options.RequireHttpsMetadata = builder.Environment.IsProduction();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = keycloakSection["Authority"],
            ValidateAudience = false,
            ValidateLifetime = true,
            NameClaimType = "preferred_username",
            RoleClaimType = "realm_access"
        };
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JwtBearer");
                logger.LogError(context.Exception, "JWT authentication failed");
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JwtBearer");
                logger.LogInformation("JWT token validated for {User}", context.Principal?.Identity?.Name);
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("JwtBearer");
                logger.LogWarning("JWT challenge issued: {Error} {ErrorDescription}", context.Error, context.ErrorDescription);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ---------------------------------------------------------------------------
// Database — EF Core (PostgreSQL or SQLite based on DatabaseProvider setting)
// ---------------------------------------------------------------------------
var dbProvider = builder.Configuration["DatabaseProvider"] ?? "PostgreSQL";

builder.Services.AddDbContext<DataNexusDbContext>(options =>
{
    if (dbProvider.Equals("SQLite", StringComparison.OrdinalIgnoreCase))
        options.UseSqlite(builder.Configuration.GetConnectionString("DataNexus"));
    else
        options.UseNpgsql(builder.Configuration.GetConnectionString("DataNexus"));
});

// ---------------------------------------------------------------------------
// Identity services
// ---------------------------------------------------------------------------
builder.Services.AddScoped<UserContext>();
builder.Services.AddSingleton<KeycloakAuthService>();

// ---------------------------------------------------------------------------
// Azure AI Inference — GitHub Models (gpt-4o)
// ---------------------------------------------------------------------------
builder.Services.Configure<GitHubModelsConfig>(
    builder.Configuration.GetSection("GitHubModels"));

builder.Services.AddSingleton(_ =>
{
    var endpoint = new Uri(
        builder.Configuration["GitHubModels:Endpoint"]
        ?? "https://models.inference.ai.azure.com");

    var credential = new AzureKeyCredential(
        builder.Configuration["GitHubModels:ApiKey"]
        ?? throw new InvalidOperationException("GitHubModels:ApiKey must be configured"));

    return new ChatCompletionsClient(endpoint, credential);
});

// ---------------------------------------------------------------------------
// HTTP clients for plugin I/O
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient("DataNexusInput");
builder.Services.AddHttpClient("DataNexusOutput");

// ---------------------------------------------------------------------------
// Core services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<SkillRegistry>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<PipelineRegistry>();
builder.Services.AddSingleton<TaskHistoryRegistry>();

// External agent execution
builder.Services.Configure<ExternalAgentOptions>(
    builder.Configuration.GetSection(ExternalAgentOptions.SectionName));
builder.Services.AddScoped<ExternalProcessRunner>();

// Plugins
builder.Services.AddScoped<InputProcessorPlugin>();
builder.Services.AddScoped<OutputIntegratorPlugin>();

// Agents & Engine
builder.Services.Configure<AgentRuntimeOptions>(
    builder.Configuration.GetSection(AgentRuntimeOptions.SectionName));
builder.Services.AddSingleton<AfChatClientProvider>();
builder.Services.AddScoped<DynamicWorkflowBuilder>();
builder.Services.AddScoped<DataNexusEngine>();
builder.Services.AddScoped<AgentFrameworkExecutionRuntime>();
builder.Services.AddScoped<IAgentExecutionRuntime, AgentExecutionRuntimeSelector>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Startup: create DB schema and seed built-in skills from .github/skills/public/
// ---------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DataNexusDbContext>();
    await db.Database.EnsureCreatedAsync();
}

var skillRegistry = app.Services.GetRequiredService<SkillRegistry>();
var seedPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", ".github", "skills", "public"));
await skillRegistry.InitializeAsync(seedPath);

var agentRegistry = app.Services.GetRequiredService<AgentRegistry>();
await agentRegistry.InitializeAsync();

// ---------------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------------
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<KeycloakMiddleware>();

// ---------------------------------------------------------------------------
// Minimal API endpoints
// ---------------------------------------------------------------------------
app.MapProcessingEndpoints();
app.MapSkillsEndpoints();
app.MapAgentEndpoints();
app.MapPipelineEndpoints();
app.MapTaskHistoryEndpoints();

app.Run();
