import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import App from "./App";
import { initAuth, setBackendUserId } from "@/services/auth";
import { fetchMe } from "@/services/api";
import "@/styles/global.css";

// If this window was opened by MSAL as a popup redirect target, hand control back
// to MSAL and stop — don't bootstrap Keycloak or render the React app.
const _searchParams = new URLSearchParams(window.location.search);
const _isMsalPopup =
  window.opener != null &&
  window.opener !== window &&
  (_searchParams.has("code") || _searchParams.has("error"));

if (_isMsalPopup) {
  const clientId = import.meta.env.VITE_ONEDRIVE_CLIENT_ID as string | undefined;
  if (clientId) {
    import("@azure/msal-browser").then(async ({ PublicClientApplication }) => {
      const pca = new PublicClientApplication({
        auth: {
          clientId,
          authority: "https://login.microsoftonline.com/common",
          redirectUri: window.location.origin,
        },
        cache: { cacheLocation: "sessionStorage" },
      });
      await pca.initialize();
      // MSAL sends the auth result to the opener and closes this popup automatically.
    });
  }
} else {

initAuth().then(async (authenticated) => {
  if (authenticated) {
    // Resolve the userId as the backend sees it so ownership comparisons
    // (edit/delete/publish buttons) always use the correct identity.
    try {
      const me = await fetchMe();
      setBackendUserId(me.userId);
    } catch {
      // Non-fatal: fallback to keycloak.subject used inside getUserId()
    }

    createRoot(document.getElementById("root")!).render(
      <StrictMode>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </StrictMode>,
    );
  }
});

} // end of non-popup branch
