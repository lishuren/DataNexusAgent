import { useCallback, useEffect, useRef, useState } from "react";

interface OneDriveFilePickerProps {
  accept?: string;
  onChange: (dataUrl: string) => void;
  onFileName?: (name: string) => void;
}

const CLIENT_ID = import.meta.env.VITE_ONEDRIVE_CLIENT_ID as string | undefined;

// OneDrive File Picker v8 uses a popup window that communicates via postMessage.
// See: https://learn.microsoft.com/en-us/onedrive/developer/controls/file-pickers/js-v72/open-file

/** Convert a Blob to a data URL. */
function blobToDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(blob);
  });
}

export default function OneDriveFilePicker({ accept, onChange, onFileName }: OneDriveFilePickerProps) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const popupRef = useRef<Window | null>(null);

  // Map accept string (e.g. ".xlsx,.csv,.json") to OneDrive filter extensions
  const fileExtensions = accept
    ?.split(",")
    .map((e) => e.trim().replace(/^\./, ""))
    .filter(Boolean);

  const handlePick = useCallback(async () => {
    if (!CLIENT_ID) {
      setError("OneDrive not configured");
      return;
    }

    setLoading(true);
    setError(null);

    try {
      // Open the OneDrive picker in a popup window.
      // We use the v8 File Picker which communicates via postMessage.
      const authority = "https://login.microsoftonline.com/common";
      const redirectUri = window.location.origin + "/onedrive-picker-redirect.html";
      const scope = "Files.Read.All";

      const pickerParams = new URLSearchParams({
        client_id: CLIENT_ID,
        response_type: "token",
        redirect_uri: redirectUri,
        scope: `openid ${scope}`,
        prompt: "select_account",
      });

      const authUrl = `${authority}/oauth2/v2.0/authorize?${pickerParams.toString()}`;

      // Open popup for OAuth
      const popup = window.open(authUrl, "onedrive-auth", "width=600,height=700,popup=yes");
      if (!popup) {
        throw new Error("Popup blocked — please allow popups for this site");
      }
      popupRef.current = popup;

      // Listen for the OAuth redirect with the access token
      const accessToken = await new Promise<string>((resolve, reject) => {
        const interval = setInterval(() => {
          if (popup.closed) {
            clearInterval(interval);
            reject(new Error("Authentication cancelled"));
          }
          try {
            const hash = popup.location.hash;
            if (hash && hash.includes("access_token")) {
              clearInterval(interval);
              const params = new URLSearchParams(hash.substring(1));
              const token = params.get("access_token");
              popup.close();
              if (token) resolve(token);
              else reject(new Error("No access token received"));
            }
          } catch {
            // Cross-origin — popup hasn't redirected yet, ignore
          }
        }, 200);

        // Timeout after 2 minutes
        setTimeout(() => {
          clearInterval(interval);
          popup.close();
          reject(new Error("Authentication timed out"));
        }, 120_000);
      });

      // Use Microsoft Graph API to show a picker-like experience:
      // List recent files from OneDrive root
      const pickerResponse = await fetch(
        "https://graph.microsoft.com/v1.0/me/drive/root/children?" +
          new URLSearchParams({
            $select: "name,id,size,file,@microsoft.graph.downloadUrl",
            $top: "50",
            $orderby: "lastModifiedDateTime desc",
          }),
        { headers: { Authorization: `Bearer ${accessToken}` } },
      );

      if (!pickerResponse.ok) {
        throw new Error(`OneDrive API error: ${pickerResponse.status}`);
      }

      const listing = (await pickerResponse.json()) as {
        value: Array<{
          name: string;
          id: string;
          "@microsoft.graph.downloadUrl"?: string;
          file?: { mimeType: string };
        }>;
      };

      // Filter to files only (exclude folders) and optionally filter by extension
      let files = listing.value.filter((item) => item.file);
      if (fileExtensions?.length) {
        files = files.filter((item) =>
          fileExtensions.some((ext) => item.name.toLowerCase().endsWith(`.${ext}`)),
        );
      }

      if (files.length === 0) {
        throw new Error("No matching files found in your OneDrive root folder");
      }

      // For now, show a simple native prompt to pick from the file list.
      // A full modal picker UI can be added later.
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
      if (!downloadUrl) {
        throw new Error("Could not get download URL for the selected file");
      }

      // Download the file content
      const fileResponse = await fetch(downloadUrl);
      if (!fileResponse.ok) throw new Error("Failed to download file from OneDrive");

      const blob = await fileResponse.blob();
      const dataUrl = await blobToDataUrl(blob);

      onFileName?.(selected.name);
      onChange(dataUrl);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
      popupRef.current = null;
    }
  }, [onChange, onFileName, fileExtensions]);

  // Cleanup popup on unmount
  useEffect(() => {
    return () => {
      popupRef.current?.close();
    };
  }, []);

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
