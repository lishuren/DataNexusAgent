using System.Diagnostics;

namespace DataNexus.Identity;

public sealed class KeycloakMiddleware(
    RequestDelegate next,
    KeycloakAuthService authService,
    ILogger<KeycloakMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, UserContext userContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var extracted = authService.ExtractUserContext(context.User);

            userContext.UserId = extracted.UserId;
            userContext.Email = extracted.Email;
            userContext.DisplayName = extracted.DisplayName;
            userContext.Roles = extracted.Roles;
            userContext.IsAuthenticated = true;

            // Tag distributed traces with the authenticated user
            Activity.Current?.SetTag("user.id", extracted.UserId);

            logger.LogInformation(
                "[User: {UserId}] {Method} {Path} — agent relay initiated",
                extracted.UserId,
                context.Request.Method,
                context.Request.Path);
        }

        await next(context);

        if (userContext.IsAuthenticated)
        {
            logger.LogInformation(
                "[User: {UserId}] {Method} {Path} — completed {StatusCode}",
                userContext.UserId,
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode);
        }
    }
}
