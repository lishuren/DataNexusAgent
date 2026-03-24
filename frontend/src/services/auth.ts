import Keycloak from "keycloak-js";

const keycloak = new Keycloak({
  url: import.meta.env.VITE_KEYCLOAK_URL ?? "https://cdskc.gprddigital.com",
  realm: import.meta.env.VITE_KEYCLOAK_REALM ?? "DashboardPlus",
  clientId: import.meta.env.VITE_KEYCLOAK_CLIENT_ID ?? "datanexus",
});

let initialized = false;

export async function initAuth(): Promise<boolean> {
  if (initialized) return keycloak.authenticated ?? false;
  initialized = true;

  const authenticated = await keycloak.init({
    onLoad: "login-required",
    checkLoginIframe: false,
    pkceMethod: false,
  });

  // Auto-refresh token 30s before expiry.
  // If the user has been removed from Keycloak, updateToken will fail
  // and we force a logout so the session cannot continue.
  setInterval(async () => {
    if (keycloak.authenticated) {
      try {
        await keycloak.updateToken(30);
      } catch {
        console.warn("Token refresh failed — user session is no longer valid");
        keycloak.logout();
      }
    }
  }, 10_000);

  return authenticated;
}

export function getToken(): string | undefined {
  return keycloak.token;
}

// Backend-confirmed userId — set once after login via /api/me so ownership
// comparisons use the exact same value the backend stores (respects UserIdClaim config).
let _backendUserId: string | undefined;

export function setBackendUserId(id: string): void {
  _backendUserId = id;
}

export function getUserId(): string | undefined {
  return _backendUserId ?? keycloak.subject;
}

export function getDisplayName(): string | undefined {
  const p = keycloak.tokenParsed;
  if (!p) return undefined;
  const full = p["name"] as string | undefined;
  if (full) return full;
  const given = p["given_name"] as string | undefined;
  const family = p["family_name"] as string | undefined;
  if (given || family) return [given, family].filter(Boolean).join(" ");
  return p["preferred_username"] as string | undefined;
}

/** Fetches the full user profile from Keycloak's /userinfo endpoint.
 *  Falls back to token claims if the endpoint is unavailable.
 */
export async function loadDisplayName(): Promise<string | undefined> {
  try {
    const profile = await keycloak.loadUserProfile();

    // Log available profile fields in dev so we can see what Keycloak provides
    if (import.meta.env.DEV) {
      console.debug("[DataNexus] Keycloak profile:", profile);
      console.debug("[DataNexus] Token claims:", keycloak.tokenParsed);
    }

    const full = [profile.firstName, profile.lastName].filter(Boolean).join(" ");
    if (full.trim()) return full.trim();
    // Try email — often more useful than a raw username
    if (profile.email) return profile.email;
    return (profile.username as string | undefined) ?? getDisplayName();
  } catch {
    return getDisplayName();
  }
}

export function logout(): void {
  keycloak.logout();
}

export { keycloak };
