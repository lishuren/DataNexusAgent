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
  return keycloak.tokenParsed?.["preferred_username"] as string | undefined;
}

export function logout(): void {
  keycloak.logout();
}

export { keycloak };
