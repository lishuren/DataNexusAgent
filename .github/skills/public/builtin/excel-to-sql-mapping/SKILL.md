---
name: excel-to-sql-mapping
description: Maps common spreadsheet columns and values to SQL-friendly names and normalized data types.
---

# ExcelToSqlMapping

## Purpose

Maps common Excel column patterns to SQL-compatible field names and types.

## Rules

- Column headers with spaces should be converted to snake_case.
- Date columns in any format should be normalized to ISO-8601 (`yyyy-MM-dd`).
- Currency values should strip symbols (`$`, `EUR`, `GBP`) and store as decimal.
- Boolean-like values (`Yes`/`No`, `True`/`False`, `1`/`0`) should normalize to `true`/`false`.
- Empty cells should map to `null` in JSON output.
- Column names matching SQL reserved words should be suffixed with `_col`.

## Type Mapping

| Excel Pattern       | SQL Type        | JSON Type         |
| ------------------- | --------------- | ----------------- |
| Integer numbers     | INT             | number            |
| Decimal numbers     | DECIMAL(18,4)   | number            |
| Dates               | DATE            | string (ISO-8601) |
| Text < 255 chars    | NVARCHAR(255)   | string            |
| Text >= 255 chars   | NVARCHAR(MAX)   | string            |
| Boolean-like        | BIT             | boolean           |