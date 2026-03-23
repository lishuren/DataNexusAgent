using System.Security.Claims;
using System.Text.Json;

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

        var roles = ExtractRealmRoles(principal);

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

    /// <summary>
    /// Extracts roles from Keycloak's <c>realm_access</c> claim, which is a JSON object
    /// like <c>{"roles":["admin","user"]}</c>.
    /// </summary>
    private List<string> ExtractRealmRoles(ClaimsPrincipal principal)
    {
        var realmAccess = principal.FindFirstValue("realm_access");
        if (string.IsNullOrEmpty(realmAccess))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(realmAccess);
            if (doc.RootElement.TryGetProperty("roles", out var rolesElement) &&
                rolesElement.ValueKind == JsonValueKind.Array)
            {
                return rolesElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToList();
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse realm_access claim as JSON");
        }

        return [];
    }

    public bool ValidateUserOwnership(UserContext user, string resourceOwnerId) =>
        string.Equals(user.UserId, resourceOwnerId, StringComparison.Ordinal);
}
