import { useCallback, useRef, useState } from "react";
import {
  type AccountInfo,
  type IPublicClientApplication,
  InteractionRequiredAuthError,
  PublicClientApplication,
} from "@azure/msal-browser";
import { toCompressedDataUrl } from "@/utils/compressFile";

interface OneDriveFilePickerProps {
  accept?: string;
  onChange: (dataUrl: string) => void;
  onFileName?: (name: string) => void;
}

const CLIENT_ID = import.meta.env.VITE_ONEDRIVE_CLIENT_ID as string | undefined;

const GRAPH_SCOPES = ["Files.Read"];

/** Lazily create and initialise a single MSAL instance per client ID. */
let msalInstance: IPublicClientApplication | null = null;

async function getMsalInstance(): Promise<IPublicClientApplication> {
  if (msalInstance) return msalInstance;

  console.log("[OneDrive] Initializing MSAL...");
  const pca = new PublicClientApplication({
    auth: {
      clientId: CLIENT_ID!,
      authority: "https://login.microsoftonline.com/common",
      redirectUri: `${window.location.origin}/msal-redirect.html`,
    },
    // localStorage so the redirect page and main page share the MSAL cache.
    // After first login, acquireTokenSilent works without opening any window.
    cache: { cacheLocation: "localStorage" },
  });

  await pca.initialize();
  msalInstance = pca;
  console.log("[OneDrive] MSAL initialized");
  return pca;
}

/**
 * Open the dedicated auth window and wait for the token via BroadcastChannel.
 * The auth window uses loginRedirect (not popup), so it survives multi-hop
 * corporate SSO chains (ADFS, Okta, etc.) that break window.opener.
 */
function acquireTokenViaRedirect(): Promise<string> {
  return new Promise((resolve, reject) => {
    console.log("[OneDrive] Opening auth window for redirect login...");
    const channel = new BroadcastChannel("datanexus-msal");

    // Center the auth window on screen
    const w = 520, h = 700;
    const left = Math.round(window.screenX + (window.outerWidth - w) / 2);
    const top = Math.round(window.screenY + (window.outerHeight - h) / 2);
    const authWindow = window.open(
      "/msal-redirect.html",
      "datanexus-msal-auth",
      `width=${w},height=${h},left=${left},top=${top}`,
    );

    if (!authWindow) {
      channel.close();
      reject(new Error("Popup blocked — please allow popups for this site"));
      return;
    }

    const timer = setTimeout(() => {
      channel.close();
      reject(new Error("Authentication timed out — please try again"));
    }, 600000); // 10 minutes

    // NOTE: we intentionally do NOT poll authWindow.closed here.
    // During multi-hop SSO (Microsoft → ADFS → Okta), the window navigates
    // cross-origin, which causes some browsers to report .closed as true
    // even though the window is still open. We rely solely on:
    // - BroadcastChannel for success/error
    // - Timeout as ultimate fallback

    channel.onmessage = (event: MessageEvent) => {
      if (event.data?.type === "token") {
        console.log("[OneDrive] Received token from auth window");
        clearTimeout(timer);
        channel.close();
        try { authWindow.close(); } catch { /* ignore */ }
        resolve(event.data.accessToken as string);
      } else if (event.data?.type === "error") {
        console.error("[OneDrive] Auth window error:", event.data.message);
        clearTimeout(timer);
        channel.close();
        try { authWindow.close(); } catch { /* ignore */ }
        reject(new Error(event.data.message || "Authentication failed"));
      }
    };
  });
}

/** Acquire a Graph access token. Silent first, then redirect window if needed. */
async function acquireToken(): Promise<string> {
  const msal = await getMsalInstance();
  const accounts = msal.getAllAccounts();
  const account: AccountInfo | undefined = accounts[0];
  console.log("[OneDrive] acquireToken — cached accounts:", accounts.length);

  if (account) {
    try {
      console.log("[OneDrive] Trying silent token for:", account.username);
      const result = await msal.acquireTokenSilent({ scopes: GRAPH_SCOPES, account });
      console.log("[OneDrive] Silent token OK");
      return result.accessToken;
    } catch (e) {
      console.warn("[OneDrive] Silent token failed:", e);
      if (!(e instanceof InteractionRequiredAuthError)) throw e;
    }
  }

  // No cached account or silent failed — open redirect window
  return acquireTokenViaRedirect();
}

/**
 * Encode a OneDrive/SharePoint sharing URL for the Graph shares API.
 * Format: "u!" + base64url(url) (no padding, + → -, / → _)
 */
function encodeSharingUrl(url: string): string {
  const b64 = btoa(url).replace(/\+/g, "-").replace(/\//g, "_").replace(/=/g, "");
  return `u!${b64}`;
}

export default function OneDriveFilePicker({ accept, onChange, onFileName }: OneDriveFilePickerProps) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [urlInput, setUrlInput] = useState("");
  const [urlLoading, setUrlLoading] = useState(false);
  const pickingRef = useRef(false);

  const handlePick = useCallback(async () => {
    if (!CLIENT_ID || pickingRef.current) return;

    pickingRef.current = true;
    setLoading(true);
    setError(null);

    const fileExtensions = accept
      ?.split(",")
      .map((e) => e.trim().replace(/^\./, ""))
      .filter(Boolean);

    try {
      const accessToken = await acquireToken();

      const listResponse = await fetch(
        "https://graph.microsoft.com/v1.0/me/drive/root/children?" +
          new URLSearchParams({
            $select: "name,id,file,@microsoft.graph.downloadUrl",
            $top: "50",
            $orderby: "lastModifiedDateTime desc",
          }),
        { headers: { Authorization: `Bearer ${accessToken}` } },
      );

      if (!listResponse.ok) {
        throw new Error(`OneDrive API error: ${listResponse.status}`);
      }

      const listing = (await listResponse.json()) as {
        value: Array<{
          name: string;
          id: string;
          "@microsoft.graph.downloadUrl"?: string;
          file?: { mimeType: string };
        }>;
      };

      let files = listing.value.filter((item) => item.file);
      if (fileExtensions?.length) {
        files = files.filter((item) =>
          fileExtensions.some((ext) => item.name.toLowerCase().endsWith(`.${ext}`)),
        );
      }

      if (files.length === 0) {
        throw new Error("No matching files found in your OneDrive root folder");
      }

      const fileList = files.map((f, i) => `${i + 1}. ${f.name}`).join("\n");
      const choice = window.prompt(
        `Select a file from OneDrive (enter number):\n\n${fileList}`,
        "1",
      );

      if (!choice) throw new Error("Selection cancelled");
      const idx = parseInt(choice, 10) - 1;
      if (idx < 0 || idx >= files.length) throw new Error("Invalid selection");

      const selected = files[idx]!;
      const downloadUrl = selected["@microsoft.graph.downloadUrl"];
      if (!downloadUrl) throw new Error("Could not get download URL for the selected file");

      const fileResponse = await fetch(downloadUrl);
      if (!fileResponse.ok) throw new Error("Failed to download file from OneDrive");

      const blob = await fileResponse.blob();
      const dataUrl = await toCompressedDataUrl(blob, selected.name);

      onFileName?.(selected.name);
      onChange(dataUrl);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      if (msg !== "Selection cancelled") setError(msg);
    } finally {
      setLoading(false);
      pickingRef.current = false;
    }
  }, [accept, onChange, onFileName]);

  const handleUrlResolve = useCallback(async () => {
    const url = urlInput.trim();
    if (!url || urlLoading || pickingRef.current) return;

    pickingRef.current = true;
    setUrlLoading(true);
    setError(null);

    try {
      const encoded = encodeSharingUrl(url);
      const sharesUrl = `https://graph.microsoft.com/v1.0/shares/${encoded}/driveItem`;

      console.log("[OneDrive] Trying unauthenticated shares API...");
      let metaResponse = await fetch(sharesUrl);
      console.log("[OneDrive] Unauthenticated response:", metaResponse.status);

      if (metaResponse.status === 401 || metaResponse.status === 403) {
        console.log("[OneDrive] Auth required, acquiring token...");
        const accessToken = await acquireToken();
        console.log("[OneDrive] Token acquired, retrying with auth...");
        metaResponse = await fetch(sharesUrl, { headers: { Authorization: `Bearer ${accessToken}` } });
        console.log("[OneDrive] Authenticated response:", metaResponse.status);
      }

      if (!metaResponse.ok) {
        throw new Error(
          metaResponse.status === 403
            ? "Access denied — sign in with an account that has access to this file"
            : `Could not resolve sharing URL (${metaResponse.status})`,
        );
      }

      const driveItem = (await metaResponse.json()) as {
        name: string;
        "@microsoft.graph.downloadUrl"?: string;
      };
      console.log("[OneDrive] Resolved file:", driveItem.name);

      const downloadUrl = driveItem["@microsoft.graph.downloadUrl"];
      if (!downloadUrl) throw new Error("Could not get download URL from sharing link");

      console.log("[OneDrive] Downloading file...");
      const fileResponse = await fetch(downloadUrl);
      if (!fileResponse.ok) throw new Error("Failed to download file from sharing link");

      const blob = await fileResponse.blob();
      console.log("[OneDrive] Downloaded", blob.size, "bytes, compressing...");
      const dataUrl = await toCompressedDataUrl(blob, driveItem.name);

      onFileName?.(driveItem.name);
      onChange(dataUrl);
      setUrlInput("");
      console.log("[OneDrive] File loaded successfully:", driveItem.name);
    } catch (err) {
      console.error("[OneDrive] URL resolve failed:", err);
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setUrlLoading(false);
      pickingRef.current = false;
    }
  }, [urlInput, urlLoading, onChange, onFileName]);

  if (!CLIENT_ID) {
    return (
      <div className="cloud-picker cloud-picker--disabled">
        <span className="cloud-picker__icon">☁️</span>
        <span className="cloud-picker__text">OneDrive not configured</span>
      </div>
    );
  }

  return (
    <div className="onedrive-picker-container">
      <button
        type="button"
        className="cloud-picker cloud-picker--onedrive"
        onClick={handlePick}
        disabled={loading || urlLoading}
      >
        <span className="cloud-picker__icon">{loading ? "⏳" : "📂"}</span>
        <span className="cloud-picker__text">
          {loading ? "Connecting to OneDrive…" : "Browse OneDrive files"}
        </span>
      </button>

      <div className="onedrive-url-section">
        <span className="onedrive-url-divider">or paste a sharing link</span>
        <div className="onedrive-url-row">
          <input
            type="url"
            className="onedrive-url-input"
            placeholder="https://…sharepoint.com/:x:/g/personal/…"
            value={urlInput}
            onChange={(e) => setUrlInput(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") handleUrlResolve(); }}
            disabled={loading || urlLoading}
          />
          <button
            type="button"
            className="onedrive-url-btn"
            onClick={handleUrlResolve}
            disabled={!urlInput.trim() || loading || urlLoading}
          >
            {urlLoading ? "⏳" : "Load"}
          </button>
        </div>
      </div>

      {error && <span className="cloud-picker__error">{error}</span>}
    </div>
  );
}
