import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import App from "./App";
import { initAuth } from "@/services/auth";
import "@/styles/global.css";

initAuth().then((authenticated) => {
  if (authenticated) {
    createRoot(document.getElementById("root")!).render(
      <StrictMode>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </StrictMode>,
    );
  }
});
