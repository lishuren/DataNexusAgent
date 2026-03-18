using System.Security.Claims;

namespace DataNexus.Identity;

public sealed class KeycloakAuthService(
    IConfiguration configuration,
    ILogger<KeycloakAuthService> logger)
{
    private readonly string _userIdClaim = configuration["Keycloak:UserIdClaim"] ?? "sub";

    public UserContext ExtractUserContext(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            logger.LogWarning("Unauthenticated request — no user context extracted");
            return new UserContext { IsAuthenticated = false };
        }

        var userId = principal.FindFirstValue(_userIdClaim) ?? string.Empty;
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
        var displayName = principal.FindFirstValue("preferred_username") ?? string.Empty;

        var roles = principal.FindAll("realm_access")
            .Select(c => c.Value)
            .ToList();

        logger.LogInformation(
            "[User: {UserId}] Context extracted — display={DisplayName}, roles={RoleCount}",
            userId, displayName, roles.Count);

        return new UserContext
        {
            UserId = userId,
            Email = email,
            DisplayName = displayName,
            Roles = roles,
            IsAuthenticated = true
        };
    }

    public bool ValidateUserOwnership(UserContext user, string resourceOwnerId) =>
        string.Equals(user.UserId, resourceOwnerId, StringComparison.Ordinal);
}
