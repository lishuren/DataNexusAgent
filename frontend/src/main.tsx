import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import App from "./App";
import { initAuth, setBackendUserId } from "@/services/auth";
import { fetchMe } from "@/services/api";
import "@/styles/global.css";

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
