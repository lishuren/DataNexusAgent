/**
 * MSAL redirect auth handler — runs inside the auth window only.
 *
 * Flow:
 * 1. Main page opens this page in a new window
 * 2. First load: no hash → call loginRedirect → user goes through SSO chain
 * 3. After SSO: browser redirects back here with #code=...
 * 4. handleRedirectPromise() exchanges code for token
 * 5. Token is sent to main page via BroadcastChannel
 * 6. Window closes itself
 *
 * This avoids MSAL's popup flow entirely — no window.opener dependency,
 * so multi-hop corporate SSO (ADFS, Okta, etc.) works perfectly.
 */

const CLIENT_ID = import.meta.env.VITE_ONEDRIVE_CLIENT_ID as string | undefined;
const GRAPH_SCOPES = ["Files.Read", "Files.Read.All"];

const statusEl = document.getElementById("status");

if (CLIENT_ID) {
  import("@azure/msal-browser").then(async ({ PublicClientApplication }) => {
    const channel = new BroadcastChannel("datanexus-msal");

    console.log("[msal-redirect] Initializing MSAL...");
    const pca = new PublicClientApplication({
      auth: {
        clientId: CLIENT_ID,
        authority: "https://login.microsoftonline.com/common",
        redirectUri: `${window.location.origin}/msal-redirect.html`,
        navigateToLoginRequestUrl: false,
      },
      cache: { cacheLocation: "localStorage" },
    });

    await pca.initialize();

    try {
      const result = await pca.handleRedirectPromise();

      if (result) {
        // Returning from SSO redirect with a token
        console.log("[msal-redirect] Auth complete, user:", result.account?.username);
        if (statusEl) statusEl.textContent = "Sign-in complete! Closing...";

        channel.postMessage({ type: "token", accessToken: result.accessToken });
        channel.close();

        setTimeout(() => window.close(), 500);
      } else {
        // Fresh load — start the SSO redirect chain
        console.log("[msal-redirect] Starting login redirect...");
        if (statusEl) statusEl.textContent = "Redirecting to sign-in...";

        await pca.loginRedirect({
          scopes: GRAPH_SCOPES,
          prompt: "select_account",
        });
      }
    } catch (err) {
      console.error("[msal-redirect] Error:", err);
      const message = err instanceof Error ? err.message : String(err);
      if (statusEl) statusEl.textContent = `Sign-in failed: ${message}`;

      channel.postMessage({ type: "error", message });
      channel.close();
    }
  });
} else {
  if (statusEl) statusEl.textContent = "OneDrive is not configured.";
}
