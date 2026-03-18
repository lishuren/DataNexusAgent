import Keycloak from "keycloak-js";

const keycloak = new Keycloak({
  url: import.meta.env.VITE_KEYCLOAK_URL ?? "https://your-keycloak-server",
  realm: import.meta.env.VITE_KEYCLOAK_REALM ?? "datanexus",
  clientId: import.meta.env.VITE_KEYCLOAK_CLIENT_ID ?? "datanexus-frontend",
});

let initialized = false;

export async function initAuth(): Promise<boolean> {
  if (initialized) return keycloak.authenticated ?? false;
  initialized = true;

  const authenticated = await keycloak.init({
    onLoad: "login-required",
    checkLoginIframe: false,
  });

  // Auto-refresh token 30s before expiry
  setInterval(async () => {
    if (keycloak.authenticated) {
      await keycloak.updateToken(30);
    }
  }, 10_000);

  return authenticated;
}

export function getToken(): string | undefined {
  return keycloak.token;
}

export function getUserId(): string | undefined {
  return keycloak.subject;
}

export function getDisplayName(): string | undefined {
  return keycloak.tokenParsed?.["preferred_username"] as string | undefined;
}

export function logout(): void {
  keycloak.logout();
}

export { keycloak };
