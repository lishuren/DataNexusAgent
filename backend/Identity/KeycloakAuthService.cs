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

        var userId = FindUserId(principal);
        var email = FindClaimValue(principal, ClaimTypes.Email, "email") ?? string.Empty;
        var displayName = FindClaimValue(principal, "preferred_username", "name") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(userId))
        {
            logger.LogWarning(
                "Authenticated principal missing usable user id claim. Configured claim: {Claim}. Claims: {Claims}",
                _userIdClaim,
                string.Join(", ", principal.Claims.Select(c => c.Type)));

            return new UserContext
            {
                IsAuthenticated = false,
                Email = email,
                DisplayName = displayName,
            };
        }

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

    private string FindUserId(ClaimsPrincipal principal)
    {
        // Prefer configured claim, then common JWT/.NET mapped identifiers.
        return FindClaimValue(
                principal,
                _userIdClaim,
                "sub",
                ClaimTypes.NameIdentifier,
                "oid",
                "preferred_username")
            ?? string.Empty;
    }

    private static string? FindClaimValue(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            if (string.IsNullOrWhiteSpace(claimType))
                continue;

            var value = principal.FindFirstValue(claimType);
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            // Some providers emit equivalent claim types with different casing.
            value = principal.Claims
                .FirstOrDefault(c => string.Equals(c.Type, claimType, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
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
