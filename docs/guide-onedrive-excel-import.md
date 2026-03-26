# Guide: Import & Parse Invoice Excel from OneDrive

This walkthrough extends the [Invoice Excel Parser guide](guide-invoice-excel-parser.md) by adding
a **OneDrive file picker** so users can select Excel files directly from their OneDrive account
instead of uploading from their local machine.

**Prerequisites:**
- You have the `InvoiceExcelExtractor` skill already created (see the base guide)
- A Microsoft Azure AD app registration with `Files.Read.All` delegated permission

---

## Step 1 — Configure the OneDrive App Registration

If you haven't already, register an app in Azure AD:

1. Go to [Azure Portal → App registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **New registration**
3. Set:
   - **Name:** `DataNexus OneDrive Picker`
   - **Supported account types:** Accounts in any organizational directory and personal Microsoft accounts
   - **Redirect URI:** Select **Single-page application (SPA)** and enter:
     ```
     http://localhost:5173/msal-redirect.html
     ```
     (This is a dedicated lightweight page that only handles MSAL auth — it does not
     load the full app, so corporate SSO flows complete cleanly in the popup.)
4. After creation, copy the **Application (client) ID**
5. Go to **API permissions** → **Add a permission** → **Microsoft Graph** → **Delegated** → select `Files.Read`
6. Click **Grant admin consent** (or have an admin do this)

> **Why `Files.Read` and not `Files.Read.All`?** `Files.Read` lets a user read their own files
> (user-consentable, no admin approval in most tenants). `Files.Read.All` lets the app read
> *other users'* files and typically requires admin consent. For this use case, `Files.Read` is
> the correct minimum-privilege scope.

---

## Step 2 — Configure the Frontend Environment

Create or edit `frontend/.env` and add your client ID:

```env
VITE_ONEDRIVE_CLIENT_ID=your-azure-app-client-id-here
```

Restart the Vite dev server after saving.

> **Note:** If this variable is not set, the OneDrive picker button renders as disabled with a
> "OneDrive not configured" message — there is no crash or error.

---

## Step 3 — Create the Agent with OneDrive Picker

Go to **Agents** page (`http://localhost:5173/agents`).

Fill in the "Create Agent" form:

| Field | Value |
|-------|-------|
| **Name** | `Invoice Parser (OneDrive)` |
| **Icon** | `☁️` |
| **Description** | `Extracts key fields from invoice Excel files — supports local upload and OneDrive` |
| **Execution Type** | `Llm` |
| **System Prompt** | *(same as base guide — see below)* |
| **Plugins** | `InputProcessor` |
| **Skills** | `InvoiceExcelExtractor` |
| **UI Schema** | *(see below — includes OneDrive field)* |

### System Prompt

```
You are an Invoice Parsing Agent. Your job is to:
1. Receive parsed Excel data from the InputProcessor plugin.
2. Follow the InvoiceExcelExtractor skill instructions to identify the target fields.
3. Extract: Invoice Date, Corporate Exchange Rate, Total Services in CNY,
   Total Services in USD, Account/Description/Market line items, and Grand Total.
4. Return ONLY valid JSON — no markdown fences, no explanation text.

If a field cannot be found, set it to null.
```

### UI Schema

```json
[
  {
    "key": "file",
    "label": "Upload Invoice Excel",
    "type": "file",
    "accept": ".xlsx,.xls",
    "required": true
  },
  {
    "key": "cloudFile",
    "label": "Or Pick from OneDrive",
    "type": "onedrive-file",
    "accept": ".xlsx,.xls"
  },
  {
    "key": "outputFormat",
    "label": "Output Format",
    "type": "select",
    "options": ["JSON"]
  }
]
```

> The `onedrive-file` field type is **lazy-loaded** — the picker SDK code is only fetched when this
> agent is selected. Users can provide a file via either the local upload or the OneDrive picker.

Click **Create**.

---

## Step 4 — Run the Agent with OneDrive

Go to **Process** page (`http://localhost:5173`).

1. In the agent selector dropdown, choose **Invoice Parser (OneDrive)**.
2. The form will show three fields:
   - **Upload Invoice Excel** — local file drop zone
   - **Or Pick from OneDrive** — cloud picker with two options:
     - **Browse OneDrive files** — lists your most recent files
     - **or paste a sharing link** — paste any OneDrive/SharePoint URL directly
   - **Output Format** — leave as `JSON`

### Option A: Browse your OneDrive files

1. Click **Browse OneDrive files**.
2. A popup window opens for Microsoft sign-in (first use only — subsequent uses are silent).
3. Your most recent OneDrive root files are listed — enter the number to select.
4. The file downloads, gets compressed, and is ready to process.

### Option B: Paste a SharePoint sharing link

1. Copy a sharing URL from SharePoint or OneDrive, for example:
   ```
   https://contoso-my.sharepoint.com/:x:/g/personal/user_contoso_com/ABC123?e=xyz
   ```
2. Paste it into the **"or paste a sharing link"** input box.
3. Press **Enter** or click **Load**.
4. MSAL authenticates (popup on first use, silent thereafter), then the Graph `shares` API
   resolves the link to the actual file and downloads it.
5. Click **Process**.

> **Requirements for the sharing URL:**
> - The signed-in Microsoft account must have at least read access to the file, **or**
> - The link must be an "Anyone with the link" share (no sign-in required)

### What Happens Behind the Scenes

```
┌──────────────────────────────────────────────────────────────┐
│  1. You click "Pick from OneDrive"                           │
│                                                              │
│  2. MSAL.js opens an OAuth 2.0 popup (Auth Code + PKCE)      │
│     Microsoft sign-in → access token returned securely       │
│     On repeat use: token is refreshed silently (no popup)    │
│                                                              │
│  3. Microsoft Graph API lists your OneDrive root files       │
│     (filtered to .xlsx/.xls by the accept prop)              │
│                                                              │
│  4. You select a file → browser downloads it via Graph API   │
│     download URL (CORS-enabled, no backend needed)           │
│                                                              │
│  5. File is gzip-compressed in browser (CompressionStream)   │
│     Format: data:application/gzip;x-original-type=...;base64│
│                                                              │
│  6. Sent to backend as normal — same as a local file upload  │
│                                                              │
│  7. InputProcessor decompresses → parses Excel → structured  │
│     JSON → LLM processes with InvoiceExcelExtractor skill    │
│                                                              │
│  8. Result displayed on screen                               │
└──────────────────────────────────────────────────────────────┘
```

> **Key point:** The backend has no knowledge of OneDrive. The frontend downloads the file and
> converts it to a base64 data URL (with optional gzip compression). From the backend's perspective,
> it's identical to a local file upload.

---

## Step 5 (Optional) — Add Google Drive Too

You can offer both cloud providers by adding a `google-drive-file` field to the UI Schema:

```json
[
  {
    "key": "file",
    "label": "Upload Invoice Excel",
    "type": "file",
    "accept": ".xlsx,.xls",
    "required": true
  },
  {
    "key": "oneDriveFile",
    "label": "Or Pick from OneDrive",
    "type": "onedrive-file",
    "accept": ".xlsx,.xls"
  },
  {
    "key": "googleDriveFile",
    "label": "Or Pick from Google Drive",
    "type": "google-drive-file",
    "accept": ".xlsx,.xls"
  },
  {
    "key": "outputFormat",
    "label": "Output Format",
    "type": "select",
    "options": ["JSON"]
  }
]
```

This requires `VITE_GOOGLE_CLIENT_ID` and `VITE_GOOGLE_API_KEY` to be set in `frontend/.env`.
See `frontend/.env.example` for setup instructions.

---

## Quick Reference

| Concept | What It Does | Where It Lives |
|---------|-------------|----------------|
| **UiSchema `onedrive-file`** | Renders a OneDrive picker button (lazy-loaded) | Agent's UI Schema JSON |
| **UiSchema `google-drive-file`** | Renders a Google Drive picker button (lazy-loaded) | Agent's UI Schema JSON |
| **`VITE_ONEDRIVE_CLIENT_ID`** | Azure AD app client ID for MSAL OAuth | `frontend/.env` |
| **MSAL.js** | Auth Code + PKCE flow — tokens never exposed in URL | `@azure/msal-browser` package |
| **Compression** | Files are gzip-compressed in-browser before base64 | Automatic — transparent to agents/skills |
| **Backend** | No changes needed — cloud files arrive as data URLs | Same as local upload |

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| "OneDrive not configured" message | Set `VITE_ONEDRIVE_CLIENT_ID` in `frontend/.env` and restart Vite |
| Popup blocked | Allow popups for `localhost:5173` in your browser |
| "Authentication cancelled" | User closed the popup before completing sign-in |
| "Authentication timed out" | Sign-in wasn't completed within 2 minutes — try again |
| No files listed | The picker queries OneDrive root folder — make sure files are there, not in subfolders |
| Wrong file types shown | Check the `accept` prop in UI Schema matches the extensions you want |
| CORS error on download | This shouldn't happen — Graph API download URLs support CORS. Check browser console for details |
| Google picker not working | Ensure both `VITE_GOOGLE_CLIENT_ID` and `VITE_GOOGLE_API_KEY` are set |
