using DataNexus.Agents;
using DataNexus.Core;
using DataNexus.Endpoints;
using DataNexus.Identity;
using DataNexus.Plugins;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;
using OpenAI;

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
        var expectedAudience = keycloakSection["Audience"] ?? "datanexus";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = keycloakSection["Authority"],
            ValidateAudience = true,
            ValidateLifetime = true,
            NameClaimType = "preferred_username",
            RoleClaimType = "realm_access",
            // Keycloak puts the client_id in the "azp" (authorized party) claim,
            // not always in "aud". Accept either as valid audience.
            AudienceValidator = (audiences, token, _) =>
            {
                if (audiences.Any(a => string.Equals(a, expectedAudience, StringComparison.OrdinalIgnoreCase)))
                    return true;
                // Fall back to "azp" claim (Keycloak's authorized party)
                if (token is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt)
                    return string.Equals(jwt.GetPayloadValue<string>("azp"), expectedAudience, StringComparison.OrdinalIgnoreCase);
                return false;
            }
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
// AI — IChatClient via OpenAI SDK + Microsoft.Extensions.AI
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IChatClient>(_ =>
{
    var apiKey = builder.Configuration["GitHubModels:ApiKey"]
        ?? throw new InvalidOperationException("GitHubModels:ApiKey must be configured");
    var endpoint = new Uri(
        builder.Configuration["GitHubModels:Endpoint"]
        ?? "https://models.inference.ai.azure.com");
    var model = builder.Configuration["GitHubModels:Model"] ?? "gpt-4o";

    var openAiClient = new OpenAIClient(
        new System.ClientModel.ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = endpoint });

    return openAiClient.GetChatClient(model).AsIChatClient();
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
builder.Services.AddSingleton<OrchestrationRegistry>();
builder.Services.AddSingleton<TaskHistoryRegistry>();

// External agent execution
builder.Services.Configure<ExternalAgentOptions>(
    builder.Configuration.GetSection(ExternalAgentOptions.SectionName));
builder.Services.AddScoped<ExternalProcessRunner>();

// Plugins
builder.Services.AddScoped<InputProcessorPlugin>();
builder.Services.AddScoped<OutputIntegratorPlugin>();

// Agent engine
builder.Services.AddScoped<AgentFactory>();
builder.Services.AddScoped<PlannerService>();
builder.Services.AddScoped<DataNexusEngine>();
builder.Services.AddScoped<IAgentExecutionRuntime>(sp => sp.GetRequiredService<DataNexusEngine>());

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

// Returns the user context as seen by the backend so the frontend can do
// ownership comparisons using the exact same userId the backend stores.
app.MapGet("/api/me", (UserContext user) =>
    user.IsAuthenticated
        ? Results.Ok(new { user.UserId, user.DisplayName, user.Email })
        : Results.Unauthorized())
    .RequireAuthorization();

app.MapProcessingEndpoints();
app.MapSkillsEndpoints();
app.MapAgentEndpoints();
app.MapPipelineEndpoints();
app.MapOrchestrationEndpoints();
app.MapTaskHistoryEndpoints();

app.Run();
