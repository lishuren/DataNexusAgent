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

  const pca = new PublicClientApplication({
    auth: {
      clientId: CLIENT_ID!,
      authority: "https://login.microsoftonline.com/common",
      // MSAL handles its own redirect internally — no redirect HTML file needed.
      redirectUri: window.location.origin,
    },
    cache: { cacheLocation: "sessionStorage" },
  });

  await pca.initialize();
  msalInstance = pca;
  return pca;
}

/** Acquire a Graph access token, silently if possible, popup on first use. */
async function acquireToken(msal: IPublicClientApplication): Promise<string> {
  const accounts = msal.getAllAccounts();
  const account: AccountInfo | undefined = accounts[0];

  if (account) {
    try {
      const result = await msal.acquireTokenSilent({ scopes: GRAPH_SCOPES, account });
      return result.accessToken;
    } catch (e) {
      // Silent refresh failed (expired, policy change) — fall through to popup
      if (!(e instanceof InteractionRequiredAuthError)) throw e;
    }
  }

  // Auth Code + PKCE popup (modern, non-deprecated flow)
  const result = await msal.loginPopup({
    scopes: GRAPH_SCOPES,
    prompt: "select_account",
  });
  return result.accessToken;
}

export default function OneDriveFilePicker({ accept, onChange, onFileName }: OneDriveFilePickerProps) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Track in-flight popup so we don't open two
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
      const msal = await getMsalInstance();
      const accessToken = await acquireToken(msal);

      // List files from OneDrive root, most recently modified first
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

      // Files only (no folders), optionally filtered by extension
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

  if (!CLIENT_ID) {
    return (
      <div className="cloud-picker cloud-picker--disabled">
        <span className="cloud-picker__icon">☁️</span>
        <span className="cloud-picker__text">OneDrive not configured</span>
      </div>
    );
  }

  return (
    <button
      type="button"
      className="cloud-picker cloud-picker--onedrive"
      onClick={handlePick}
      disabled={loading}
    >
      <span className="cloud-picker__icon">{loading ? "⏳" : "📂"}</span>
      <span className="cloud-picker__text">
        {loading ? "Connecting to OneDrive…" : "Pick from OneDrive"}
      </span>
      {error && <span className="cloud-picker__error">{error}</span>}
    </button>
  );
}
