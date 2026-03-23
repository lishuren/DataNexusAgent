# Guide: Parse Invoice Excel → JSON with a Custom Skill & Agent

This walkthrough creates a **Skill** and an **Agent** in DataNexus that extracts key fields from an
invoice calculation Excel file and returns structured JSON.

**Target file:** `Invoice Calculation-February 2026-Nextep with Chengdu.xlsx`

**Fields to extract:**

| Field | Description |
|-------|-------------|
| Invoice Date | Billing period date |
| Corporate Exchange Rate | CNY ↔ USD rate used |
| Total Services Rendered in CNY | Sum of services in Chinese Yuan |
| Total Services in USD | Sum of services in US Dollars |
| Account | Account code/number |
| Account Description | Label for the account |
| Market | Market identifier |
| Grand Total | Overall invoice total |

---

## Step 1 — Create the Skill

Go to **Skills** page (`http://localhost:5173/skills`).

Fill in the form:

- **Name:** `InvoiceExcelExtractor`
- **Instructions:** paste the markdown below

```markdown
# InvoiceExcelExtractor

## Purpose

Extract key financial fields from a Nextep-style invoice calculation Excel file
and return them as structured JSON.

## Target Fields

Extract exactly these fields from the spreadsheet:

| Field                          | Where to Look                                   |
|--------------------------------|-------------------------------------------------|
| invoiceDate                    | Cell or row labeled "Invoice Date"              |
| corporateExchangeRate          | Cell or row labeled "Corporate Exchange Rate"   |
| totalServicesRenderedInCNY     | Cell or row labeled "Total Services rendered in CNY" or similar |
| totalServicesInUSD             | Cell or row labeled "Total Services in USD" or similar |
| grandTotal                     | Cell or row labeled "Grand Total"               |

Additionally, extract the **line-item table** where each row contains:

| Column              | JSON key           |
|---------------------|--------------------|
| Account             | account            |
| Account Description | accountDescription |
| Market              | market             |

## Output Format

Always respond with **valid JSON only**, using this exact structure:

```json
{
  "invoiceDate": "2026-02-01",
  "corporateExchangeRate": 7.25,
  "totalServicesRenderedInCNY": 150000.00,
  "totalServicesInUSD": 20689.66,
  "grandTotal": 20689.66,
  "lineItems": [
    {
      "account": "60001",
      "accountDescription": "Professional Services",
      "market": "Chengdu"
    }
  ]
}
```

## Rules

- Dates must be ISO-8601 format (`yyyy-MM-dd`).
- Currency values must be plain numbers (no symbols, no commas).
- If a field is not found, set its value to `null`.
- The `lineItems` array should contain one object per row in the Account table.
- Do NOT include any explanation text outside the JSON object.
```

Click **Create**.

You should see `InvoiceExcelExtractor` appear in your skills list as a **PRIVATE** skill.

---

## Step 2 — Create the Agent

Go to **Agents** page (`http://localhost:5173/agents`).

Fill in the "Create Agent" form:

| Field | Value |
|-------|-------|
| **Name** | `Invoice Parser` |
| **Icon** | `📄` |
| **Description** | `Extracts key fields from invoice Excel files and returns JSON` |
| **Execution Type** | `Llm` |
| **System Prompt** | *(see below)* |
| **Plugins** | `InputProcessor` |
| **Skills** | `InvoiceExcelExtractor` |
| **UI Schema** | *(see below)* |

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
    "type": "file",
    "name": "inputFile",
    "label": "Upload Invoice Excel",
    "accept": ".xlsx,.xls",
    "required": true
  },
  {
    "type": "select",
    "name": "outputFormat",
    "label": "Output Format",
    "options": ["JSON"]
  }
]
```

Click **Create**.

---

## Step 3 — Run the Agent

Go to **Process** page (`http://localhost:5173`).

1. In the agent selector dropdown, choose **Invoice Parser**.
2. The form will show:
   - **Upload Invoice Excel** — drop or browse for the `.xlsx` file
   - **Output Format** — leave as `JSON`
3. Click **Process**.

### What Happens Behind the Scenes

```
┌──────────────────────────────────────────────────────────┐
│  1. You upload the Excel file                            │
│                                                          │
│  2. InputProcessor plugin runs BEFORE the LLM call:     │
│     • Reads the .xlsx using ClosedXML                    │
│     • Converts every sheet/row/cell into structured JSON │
│     • Passes the JSON text as the LLM user message       │
│                                                          │
│  3. Engine builds the system prompt:                     │
│     • Agent's SystemPrompt                               │
│     • + InvoiceExcelExtractor skill markdown (injected)  │
│                                                          │
│  4. LLM (GPT-4o via GitHub Models) processes the data:  │
│     • Reads the parsed Excel JSON                        │
│     • Follows the skill instructions                     │
│     • Returns structured JSON with the target fields     │
│                                                          │
│  5. Result displayed on screen                           │
└──────────────────────────────────────────────────────────┘
```

### Expected Output

```json
{
  "invoiceDate": "2026-02-01",
  "corporateExchangeRate": 7.25,
  "totalServicesRenderedInCNY": 150000.00,
  "totalServicesInUSD": 20689.66,
  "grandTotal": 20689.66,
  "lineItems": [
    {
      "account": "60001",
      "accountDescription": "Professional Services",
      "market": "Chengdu"
    },
    {
      "account": "60002",
      "accountDescription": "Support Services",
      "market": "Chengdu"
    }
  ]
}
```

*(Actual values will depend on your Excel file content.)*

---

## Step 4 (Optional) — Publish to Marketplace

Once you're happy with the results:

1. **Publish the skill:** Go to Skills → click **Publish** on `InvoiceExcelExtractor`.
2. **Publish the agent:** Go to Agents → click **Publish** on `Invoice Parser`.

Other users in your Keycloak realm can then:
- **Clone** the skill and agent to their own workspace
- Use the agent directly from the Process page

---

## Quick Reference

| Concept | What It Does | Where It Lives |
|---------|-------------|----------------|
| **Skill** (`InvoiceExcelExtractor`) | Tells the LLM *what* to extract and *how* to format it | Skills page → injected into prompt |
| **Plugin** (`InputProcessor`) | Parses the raw `.xlsx` binary into text the LLM can read | Configured on the agent → runs before LLM |
| **Agent** (`Invoice Parser`) | Ties the skill + plugin together with a system prompt and UI | Agents page → used on Process page |
| **Process page** | Where you upload the file and get the JSON result | `http://localhost:5173` |

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| No output / empty result | Check that `InputProcessor` is in the Plugins field |
| LLM returns markdown instead of JSON | Update system prompt to emphasize "ONLY valid JSON" |
| Missing fields in output | Edit the skill to add more specific cell/row hints for your Excel layout |
| Agent not visible | Make sure you created it (check Agents page list) |
| File upload doesn't work | Ensure UI Schema has `"type": "file"` with correct `accept` |
