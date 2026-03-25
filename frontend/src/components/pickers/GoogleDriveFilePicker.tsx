import { useCallback, useRef, useState } from "react";
import { toCompressedDataUrl } from "@/utils/compressFile";

interface GoogleDriveFilePickerProps {
  accept?: string;
  onChange: (dataUrl: string) => void;
  onFileName?: (name: string) => void;
}

const GOOGLE_CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID as string | undefined;
const GOOGLE_API_KEY = import.meta.env.VITE_GOOGLE_API_KEY as string | undefined;

// Google Picker API + Google Identity Services (GIS)
// Picker: https://developers.google.com/drive/picker/guides/overview
// GIS:    https://developers.google.com/identity/oauth2/web/guides/overview

const GAPI_SCRIPT = "https://apis.google.com/js/api.js";
const GIS_SCRIPT = "https://accounts.google.com/gsi/client";

/** Dynamically load a script if not already present. */
function loadScript(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    if (document.querySelector(`script[src="${CSS.escape(src)}"]`)) {
      resolve();
      return;
    }
    const s = document.createElement("script");
    s.src = src;
    s.async = true;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error(`Failed to load: ${src}`));
    document.head.appendChild(s);
  });
}

/** Map accept extensions to Google Drive MIME types for picker filtering. */
function extensionsToMimeTypes(accept?: string): string[] {
  if (!accept) return [];
  const map: Record<string, string> = {
    xlsx: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    xls: "application/vnd.ms-excel",
    csv: "text/csv",
    json: "application/json",
    pdf: "application/pdf",
    doc: "application/msword",
    docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    txt: "text/plain",
  };
  return accept
    .split(",")
    .map((e) => e.trim().replace(/^\./, "").toLowerCase())
    .map((ext) => map[ext])
    .filter((m): m is string => !!m);
}

// Extend Window for Google API globals
declare global {
  interface Window {
    gapi?: {
      load: (lib: string, cb: () => void) => void;
      client: { load: (api: string, version: string) => Promise<void> };
    };
    google?: {
      accounts: {
        oauth2: {
          initTokenClient: (config: {
            client_id: string;
            scope: string;
            callback: (response: { access_token?: string; error?: string }) => void;
          }) => { requestAccessToken: () => void };
        };
      };
      picker: {
        PickerBuilder: new () => GooglePickerBuilder;
        ViewId: { DOCS: string };
        Action: { PICKED: string; CANCEL: string };
        DocsView: new (viewId?: string) => GoogleDocsView;
      };
    };
  }
}

interface GoogleDocsView {
  setMimeTypes: (mimeTypes: string) => GoogleDocsView;
}

interface GooglePickerBuilder {
  addView: (view: GoogleDocsView) => GooglePickerBuilder;
  setOAuthToken: (token: string) => GooglePickerBuilder;
  setDeveloperKey: (key: string) => GooglePickerBuilder;
  setCallback: (cb: (data: GooglePickerResult) => void) => GooglePickerBuilder;
  setOrigin: (origin: string) => GooglePickerBuilder;
  build: () => { setVisible: (v: boolean) => void };
}

interface GooglePickerResult {
  action: string;
  docs?: Array<{
    id: string;
    name: string;
    mimeType: string;
  }>;
}

export default function GoogleDriveFilePicker({ accept, onChange, onFileName }: GoogleDriveFilePickerProps) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const sdkLoaded = useRef(false);
  const gapiReady = useRef(false);

  // Load Google scripts on first interaction
  const ensureScripts = useCallback(async () => {
    if (sdkLoaded.current) return;
    await Promise.all([loadScript(GAPI_SCRIPT), loadScript(GIS_SCRIPT)]);

    // Initialize gapi picker library
    await new Promise<void>((resolve) => {
      window.gapi!.load("picker", () => {
        gapiReady.current = true;
        resolve();
      });
    });

    sdkLoaded.current = true;
  }, []);

  const handlePick = useCallback(async () => {
    if (!GOOGLE_CLIENT_ID || !GOOGLE_API_KEY) {
      setError("Google Drive not configured");
      return;
    }

    setLoading(true);
    setError(null);

    try {
      await ensureScripts();

      // Step 1: Get OAuth access token via GIS popup
      const accessToken = await new Promise<string>((resolve, reject) => {
        const tokenClient = window.google!.accounts.oauth2.initTokenClient({
          client_id: GOOGLE_CLIENT_ID!,
          scope: "https://www.googleapis.com/auth/drive.readonly",
          callback: (response) => {
            if (response.error) reject(new Error(response.error));
            else if (response.access_token) resolve(response.access_token);
            else reject(new Error("No access token received"));
          },
        });
        tokenClient.requestAccessToken();
      });

      // Step 2: Open Google Picker dialog
      const selectedFile = await new Promise<{ id: string; name: string; mimeType: string }>(
        (resolve, reject) => {
          const mimeTypes = extensionsToMimeTypes(accept);
          const view = new window.google!.picker.DocsView(window.google!.picker.ViewId.DOCS);
          if (mimeTypes.length > 0) {
            view.setMimeTypes(mimeTypes.join(","));
          }

          const picker = new window.google!.picker.PickerBuilder()
            .addView(view)
            .setOAuthToken(accessToken)
            .setDeveloperKey(GOOGLE_API_KEY!)
            .setOrigin(window.location.origin)
            .setCallback((data: GooglePickerResult) => {
              if (data.action === window.google!.picker.Action.PICKED && data.docs?.[0]) {
                resolve(data.docs[0]);
              } else if (data.action === window.google!.picker.Action.CANCEL) {
                reject(new Error("Selection cancelled"));
              }
            })
            .build();

          picker.setVisible(true);
        },
      );

      // Step 3: Download the file content via Google Drive API (supports CORS)
      const downloadResponse = await fetch(
        `https://www.googleapis.com/drive/v3/files/${encodeURIComponent(selectedFile.id)}?alt=media`,
        { headers: { Authorization: `Bearer ${accessToken}` } },
      );

      if (!downloadResponse.ok) {
        throw new Error(`Google Drive download failed: ${downloadResponse.status}`);
      }

      const blob = await downloadResponse.blob();

      // Step 4: Compress & convert to base64 data URL — identical to local file upload
      const dataUrl = await toCompressedDataUrl(blob, selectedFile.name);

      onFileName?.(selectedFile.name);
      onChange(dataUrl);
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      if (msg !== "Selection cancelled") {
        setError(msg);
      }
    } finally {
      setLoading(false);
    }
  }, [accept, onChange, onFileName, ensureScripts]);

  if (!GOOGLE_CLIENT_ID || !GOOGLE_API_KEY) {
    return (
      <div className="cloud-picker cloud-picker--disabled">
        <span className="cloud-picker__icon">☁️</span>
        <span className="cloud-picker__text">Google Drive not configured</span>
      </div>
    );
  }

  return (
    <button
      type="button"
      className="cloud-picker cloud-picker--google"
      onClick={handlePick}
      disabled={loading}
    >
      <span className="cloud-picker__icon">{loading ? "⏳" : "📂"}</span>
      <span className="cloud-picker__text">
        {loading ? "Connecting to Google Drive…" : "Pick from Google Drive"}
      </span>
      {error && <span className="cloud-picker__error">{error}</span>}
    </button>
  );
}
