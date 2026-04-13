---
name: invoice-excel-extractor
description: Extract key financial fields from a Nextep-style invoice calculation Excel file
---

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
