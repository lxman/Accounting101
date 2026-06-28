# Interchange Framework + CSV Statement Import — Slice 4a — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Context

Bank reconciliation is built in four slices; Slices 1-3 shipped (core,
auto-match, adjustments posting). Slice 4 is statement **import**, but the user
reframed it: rather than a parse endpoint bolted onto Reconciliation, stand up a
**segregated import/export subsystem** — a new `Accounting101.Interchange`
project — with bank-statement import as its first tenant, designed to grow to
other formats (JSON, TSV) and other data types, and (later) export.

Slice 4 is therefore split into two shippable cycles under one design:

- **4a — Interchange framework + CSV statement import + preview endpoint** ← THIS SPEC
- **4b — `OfxStatementImporter`, OFX 1.x SGML only** (the user's real Wells Fargo QFX) — its own cycle, since SGML is the riskiest parser
- **4c — OFX 2.x XML** (deferred) — a second branch in the same importer, added if/when a 2.x file appears

Statement *creation* already exists and validates (Slice 1 `POST
/bank-statements`, foots-or-422). Import's job is purely **parsing** a bank
export into lines (+ any balances the format carries) for review.

## Goal

A reusable, entity- and format-pluggable interchange framework, plus a
hand-rolled CSV statement importer with a **configurable column mapping**, plus a
**parse-to-preview** import endpoint on Reconciliation that returns the parsed
lines/balances without creating anything. The user reviews, supplies any missing
balances, and submits to the existing validated statement endpoint.

## Scope

**In scope (4a):** the `Accounting101.Interchange` project (framework + the
bank-statement entity DTOs + the CSV importer + a `DelimitedReader` primitive +
DI registration); the `POST /clients/{c}/bank-statements/import?format=csv`
preview endpoint in Reconciliation.Api; unit + E2E tests.

**Out of scope (4b / later / not built):** OFX import (Slice 4b); any exporter
*implementation* (the `IExporter<T>` seam + registry slot exist, no writers);
importing non-statement entity types (the framework is generic, but bank
statements are the only registered entity in 4a); JSON/TSV importers (new
`IImporter<ImportedStatement>` classes later); header auto-detection beyond what
the configurable mapping provides; statement creation (reuses Slice 1); any GL
mutation.

## Architecture

### `Accounting101.Interchange` (new class library)

Entity-agnostic, parser-only — no HTTP, no Mongo, no module types. References
only `Microsoft.Extensions.DependencyInjection.Abstractions` (for the
`AddInterchange` extension; contracts-only, no runtime).

**Framework (generic over the produced entity `T`):**
```csharp
public enum InterchangeFormat { Csv, Ofx }                 // Json, Tsv later

public sealed record ImportResult<T>(IReadOnlyList<T> Records, IReadOnlyList<string> Warnings);

public sealed class ImportOptions                          // format-specific options bag
{
    public CsvMapping? Csv { get; init; }                  // required for CSV; null for formats that don't need it
}

public interface IImporter<T>
{
    InterchangeFormat Format { get; }
    ImportResult<T> Import(Stream source, ImportOptions options);
}

public interface IExporter<T>                              // seam now; no implementations in 4a
{
    InterchangeFormat Format { get; }
    void Export(IEnumerable<T> records, Stream destination, ExportOptions options);
}
public sealed class ExportOptions { }                      // grows later

public interface IInterchangeRegistry
{
    void Register<T>(IImporter<T> importer);
    IImporter<T>? Resolve<T>(InterchangeFormat format);    // by (typeof(T), format); null if none
}
```
`InterchangeRegistry` backs the lookup with a `Dictionary<(Type, InterchangeFormat), object>`.
Importers are stateless (options come per-call), so the registry is a singleton
populated at startup.

**Generic CSV primitive:** `DelimitedReader` — `IReadOnlyList<IReadOnlyList<string>>
ReadRows(TextReader, char delimiter)`: RFC-4180-ish — quoted fields (`"a,b"`),
escaped quotes (`""`), embedded newlines inside quotes, CRLF/LF tolerant, trailing
newline ignored. Entity-agnostic (rows of strings).

**Bank-statement entity (neutral DTOs the parsers produce — NOT the Reconciliation
`BankStatement`):**
```csharp
public sealed record ImportedStatement(
    IReadOnlyList<ImportedLine> Lines, decimal? OpeningBalance, decimal? ClosingBalance,
    DateOnly? StatementDate, string? AccountHint);

public sealed record ImportedLine(DateOnly Date, decimal Amount, string Description, string? Reference);
```
`ImportResult<ImportedStatement>.Records` being a list is meaningful — one OFX
file (4b) can carry several account statements; a CSV (4a) yields exactly one.

**Configurable CSV mapping:**
```csharp
public sealed record ColumnRef(int? Index, string? Header);   // resolve by index OR header name

public sealed record CsvMapping(
    ColumnRef Date,
    ColumnRef? Amount,            // single signed column (mutually exclusive with Debit/Credit)
    ColumnRef? Debit,            // separate debit column (paired with Credit)
    ColumnRef? Credit,
    ColumnRef Description,
    ColumnRef? Reference,
    string? DateFormat,          // null → try a fixed set of common formats
    bool HasHeader,
    char Delimiter = ',',
    ColumnRef? Status = null,    // optional status column (e.g. Wells Fargo "STATUS")
    IReadOnlyList<string>? ExcludeStatuses = null);  // rows whose Status equals one of these (case-insensitive) are skipped (e.g. ["Pending"])
```

When a `Status` column is mapped and `ExcludeStatuses` is non-empty, a row whose
status value matches (case-insensitive, trimmed) is filtered out **before** parsing
— it is not an error and does not appear in `Warnings`, it is simply excluded (so
an unreconcilable *Pending* row never enters the statement). When `Status` is not
mapped, all rows are parsed (the existing behavior).

**`CsvStatementImporter : IImporter<ImportedStatement>` (Format = Csv):**
- Reads rows via `DelimitedReader`; if `HasHeader`, the first row is the header
  (used to resolve `ColumnRef.Header` → index; that row is not a data row).
- Validates the mapping: exactly one of (`Amount`) XOR (`Debit` AND `Credit`),
  else `ArgumentException`. (No amount columns → invalid.)
- Per data row: parse the date (try `DateFormat`, else a fixed list: `yyyy-MM-dd`,
  `MM/dd/yyyy`, `yyyyMMdd`), parse the amount — signed column directly, or
  `credit − debit` (a debit reduces the bank account). Description required;
  Reference optional. A row that fails to parse (bad date/amount, missing required
  column) is **skipped and recorded in `Warnings`** with its row number + reason —
  never silently dropped, never aborts the whole import.
- Produces one `ImportedStatement` with the parsed lines and **no balances**
  (CSV carries none; OpeningBalance/ClosingBalance/StatementDate null) unless the
  mapping is later extended — out of scope for 4a.

**DI registration (`InterchangeServiceExtensions.AddInterchange`):** registers a
singleton `IInterchangeRegistry` pre-populated with `CsvStatementImporter` for
`ImportedStatement`. Future importers/exporters register here.

### Validated against real Wells Fargo exports (schema only)

The design was checked against a real Wells Fargo Checking CSV + QFX. These are
**schema facts** (no personal data) that pin the canonical test cases and pre-load
Slice 4b:

- **WF CSV layout:** header `DATE,DESCRIPTION,AMOUNT,CHECK #,STATUS`; all fields
  quoted; `DATE` = `MM/dd/yyyy`; **single signed `AMOUNT`** (− for purchases);
  `CHECK #` often empty; `STATUS` ∈ {Pending, …}. The canonical 4a test uses this
  layout with **synthetic** rows — a `CsvMapping` of `{ Date: "DATE", Amount:
  "AMOUNT", Description: "DESCRIPTION", Reference: "CHECK #", Status: "STATUS",
  ExcludeStatuses: ["Pending"], DateFormat: "MM/dd/yyyy", HasHeader: true }`. A
  test asserts the `Pending` rows are excluded and the posted rows parse with the
  right signed amounts + dates.
- **WF QFX is OFX 1.x SGML (Slice 4b):** `OFXHEADER:100, DATA:OFXSGML,
  VERSION:102, ENCODING:USASCII, CHARSET:1252`. **Leaf tags are unclosed**
  (`<TRNAMT>`, `<DTPOSTED>`, `<NAME>`, `<MEMO>`, `<FITID>`, `<TRNTYPE>`) — value
  runs to the next `<`; **aggregates are closed** (`<STMTTRN></STMTTRN>`,
  `<LEDGERBAL></LEDGERBAL>`, `<BANKACCTFROM></BANKACCTFROM>`). A **separate
  `<AVAILBAL>`** also holds a `BALAMT`/`DTASOF` — 4b must read LEDGERBAL's, not
  AVAILBAL's. Intuit tags `<INTU.BID>`/`<INTU.USERID>` mean the tag scanner must
  allow `.` in tag names. `<BANKACCTFROM><ACCTID>` → AccountHint;
  `<BANKTRANLIST><DTSTART>/<DTEND>` bound the period. OFX is posted-only (228 txns
  vs 236 CSV rows = the 8 pending CSV rows) — so the CSV `ExcludeStatuses:
  ["Pending"]` filter makes a CSV import match the OFX posted-only set.

### Reconciliation.Api — the preview endpoint

`POST /clients/{clientId:guid}/bank-statements/import` (multipart/form-data),
under the existing authorized reconciliation group (added to
`MapReconciliationEndpoints`, no Program.cs change; the endpoint disables
antiforgery for the API upload):
- Parts: `file` (the upload), `format` (`csv` for 4a), `mapping` (JSON of
  `CsvMapping`, required for CSV).
- Resolves `IImporter<ImportedStatement>` for the format; if none → 400.
- Imports the file stream; maps each `ImportedStatement` → a `StatementPreview`
  (its `ImportedLine`s → `BankStatementLineRequest`, plus the detected balances /
  statement date / account hint), and returns:
```csharp
public sealed record ImportPreviewResponse(IReadOnlyList<StatementPreview> Statements, IReadOnlyList<string> Warnings);
public sealed record StatementPreview(
    IReadOnlyList<BankStatementLineRequest> Lines,
    decimal? DetectedOpeningBalance, decimal? DetectedClosingBalance,
    DateOnly? StatementDate, string? AccountHint);
```
- **Nothing is created.** The client reviews the preview, supplies opening/closing
  balances (CSV has none), and submits to the existing `POST /bank-statements`
  (which foots-or-422).
- Registration: `AddReconciliation` also calls `AddInterchange()` (Reconciliation.Api
  references the Interchange project), so the host wires it without a Program.cs
  change. `Accounting101.Interchange` + its Tests project are added to the solution.

## Data flow

```
POST /clients/{c}/bank-statements/import  (multipart: file, format=csv, mapping=JSON)
  → registry.Resolve<ImportedStatement>(Csv)  → CsvStatementImporter
  → DelimitedReader.ReadRows → per-row map via CsvMapping (date/amount/desc/ref)
       parse failures → Warnings (row skipped)
  → ImportResult<ImportedStatement> (one statement, lines, no balances)
  → ImportPreviewResponse { statements:[{ lines, balances=null }], warnings }   (nothing created)
client reviews + supplies opening/closing → POST /bank-statements (Slice 1, foots-or-422)
```

## Error handling

- Unknown/unsupported `format`, or no importer registered for it → 400.
- Missing `file`, empty file, or (CSV) missing/invalid `mapping` JSON, or an
  invalid mapping (neither a signed Amount nor a Debit+Credit pair) → 422 with a
  clear message.
- Per-row parse problems do NOT fail the request — they accumulate in `Warnings`;
  the request returns 200 with whatever parsed. (An import where *every* row fails
  still returns 200 with zero lines + all-warnings; the user sees the parse didn't
  work and fixes the mapping.)
- The endpoint never creates a statement, so foot-validation does not apply here
  (it applies when the user submits the reviewed preview to `POST /bank-statements`).

## Testing

- **`DelimitedReader` unit tests:** plain rows; quoted field with embedded comma;
  escaped quotes (`""`); embedded newline inside quotes; CRLF vs LF; trailing
  newline; a custom delimiter (`;`).
- **`CsvStatementImporter` unit tests:** the **Wells Fargo layout** (synthetic
  rows: header `DATE,DESCRIPTION,AMOUNT,CHECK #,STATUS`, `MM/dd/yyyy`, signed
  amount, by-header refs) parses the posted lines with the right signed amounts +
  dates AND **excludes the `Pending` rows** via `Status`/`ExcludeStatuses`; a
  Debit/Credit two-column mapping combines to the right signed amounts; a
  positional (no-header, by-index) mapping; a bad date and a bad amount land in
  `Warnings` and are skipped (good rows still parsed); an invalid mapping (no
  Amount and no Debit/Credit) throws `ArgumentException`; a custom date format is
  honored.
- **E2E** (Reconciliation host): POST a small CSV (multipart, a signed-amount
  mapping) → 200 preview with the expected `BankStatementLineRequest`s and empty
  warnings; then take the previewed lines, compute opening/closing, and submit to
  the existing `POST /bank-statements` → 201 (proves the round-trip into a real,
  footing statement); a malformed-mapping request → 422; a CSV with one unparseable
  row → 200 with that row in `warnings` and the rest of the lines present.

## Success criteria

- The `Accounting101.Interchange` framework exists, builds, and registers; a
  generic `IImporter<T>`/`IExporter<T>` + `(T, format)` registry are in place, with
  `CsvStatementImporter` as the first registered importer and the `IExporter<T>`
  seam present (no writers).
- A CSV bank export parses via a configurable mapping into `ImportedStatement`
  lines, with parse failures surfaced as warnings (never silent drops, never a
  whole-file abort).
- The import endpoint returns a preview and creates nothing; the previewed lines
  submit cleanly to the existing statement endpoint.
- No GL mutation; no change to Slices 1-3 behavior or other modules. New unit +
  E2E tests green; existing suites stay green.
- The design leaves obvious, additive homes for OFX (4b), JSON/TSV importers, and
  exporters.
