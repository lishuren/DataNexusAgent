import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import { initAuth } from "@/services/auth";
import "@/styles/global.css";

initAuth().then((authenticated) => {
  if (authenticated) {
    createRoot(document.getElementById("root")!).render(
      <StrictMode>
        <App />
      </StrictMode>,
    );
  }
});
