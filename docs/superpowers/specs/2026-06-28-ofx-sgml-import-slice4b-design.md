# OFX 1.x SGML Statement Import — Slice 4b — Design

**Date:** 2026-06-28
**Status:** Approved (design)

## Context

The `Accounting101.Interchange` framework + CSV statement import shipped in Slice
4a (local `master`). Bank-statement import was split:

- 4a — framework + CSV + preview endpoint ✅
- **4b — OFX 1.x SGML import** ← THIS SPEC (the user's real Wells Fargo QFX dialect)
- 4c — OFX 2.x XML (deferred)

The framework is the "empty room": an `(entity, format)` `IImporter<T>` registry.
Adding OFX is registering one more `IImporter<ImportedStatement>` (Format = Ofx) —
no framework, endpoint, or CSV change. Real bank exports vary wildly (this slice
already targets two distinct real OFX 1.x dialects), so the OFX parser is
**tolerant by design**, not a strict spec validator.

## Goal

A hand-rolled `OfxStatementImporter` that parses an OFX **1.x SGML** bank-statement
file (the Wells Fargo QFX dialect and friends) into one or more `ImportedStatement`
records — transactions, the ledger closing balance, the statement date, and an
account hint — surfacing parse problems as warnings rather than crashing. It plugs
into the existing parse-to-preview endpoint with no endpoint change.

## Scope

**In scope:** `OfxStatementImporter` (Format = Ofx) registered in
`InterchangeRegistry.CreateDefault()`; a tolerant OFX 1.x SGML reader (header
optional, unclosed leaves, multiple statements per file, LEDGERBAL closing
balance, graceful failure); unit tests over synthetic SGML samples modeling the
real-world variety; an E2E (upload OFX → preview → round-trip submit).

**Out of scope (4c / later / not built):** OFX **2.x XML** (detected and politely
refused with a 4c message); investment statements (`INVSTMT`/`INVTRAN` — bank
`STMTTRN` only); the OFX client/server protocol (signon/MFA/requests — we parse a
downloaded file); credit-card statements (`CCSTMTRS` — bank `STMTRS` only, a
fast-follow if wanted); exporters; spreadsheet/proprietary formats (future
furniture); any GL mutation or statement creation (still parse-to-preview).

## Real-world variety this must tolerate (from real samples + the ofxparse corpus)

- **Header optional:** Wells Fargo has a full SGML header (`OFXHEADER:100,
  DATA:OFXSGML, VERSION:102, ENCODING:USASCII, CHARSET:1252`); another real sample
  starts straight at `<OFX>` with no preamble. Both must parse.
- **Unclosed leaf tags:** 1.x leaves (`<TRNAMT>`, `<DTPOSTED>`, `<NAME>`, `<MEMO>`,
  `<FITID>`, `<TRNTYPE>`, `<BALAMT>`, `<DTASOF>`) have no closing tag — a value runs
  to the next `<` (e.g. `<SEVERITY>INFO</STATUS>` → `INFO`). Aggregates
  (`<STMTTRN></STMTTRN>`, `<STMTRS></STMTRS>`, `<LEDGERBAL></LEDGERBAL>`,
  `<BANKACCTFROM></BANKACCTFROM>`) **are** closed.
- **LEDGERBAL not AVAILBAL:** both hold a `<BALAMT>`/`<DTASOF>`; the closing balance
  is LEDGERBAL's. Must scope to the LEDGERBAL block.
- **Multiple statements per file:** an aggregation file has several
  `<STMTRS>` blocks → several `ImportedStatement`s (`Records` is a list).
- **Empty / missing tags:** a transaction may omit `<FITID>` or have an empty
  `<TRNAMT>` (the `ofx-v102-empty-tags` case) — skip-with-warning, never crash.
- **Dates with time/offset:** `<DTPOSTED>` may be `YYYYMMDD`, `YYYYMMDDHHMMSS`, or
  `YYYYMMDDHHMMSS.LLL[-5:EST]` — take the first 8 digits.
- **Decimal separator:** usually `.`; a non-US-locale file (e.g. `LANGUAGE:FRA`)
  may use `,`. Parse invariant `.`; if that fails and a `,` is present, retry with
  `,` as the decimal.
- **Intuit/QFX tags:** `<INTU.BID>` etc. (dots in tag names) — harmless; the bank
  fields we read don't include them, and the leaf-scan tolerates them.
- **Error responses:** a non-zero `<CODE>` in `<STATUS>`, or a `signon_fail` /
  `error_message` file → no statements; surface a warning, not an exception.

## Architecture

All inside `Accounting101.Interchange` (still zero-dependency). The endpoint and
CSV path are untouched.

### Low-level SGML scan (a pure internal helper — `OfxScanner`)

Static, string-based, tolerant:
- `string? Leaf(string scope, string tag)` — find `<tag>` (case-insensitive) and
  return the text up to the next `<`, trimmed; null if the tag is absent. Reads
  unclosed leaves correctly.
- `IReadOnlyList<string> Blocks(string scope, string aggregate)` — return the inner
  text of each `<aggregate>…</aggregate>` (closed aggregates: STMTRS, STMTTRN,
  LEDGERBAL). Tolerant of nesting depth for the shapes we use.
- `bool TryParseOfxDate(string raw, out DateOnly date)` — take the leading 8 digits
  → `yyyyMMdd`; false if fewer than 8 leading digits.
- `bool TryParseOfxAmount(string raw, out decimal amount)` — invariant decimal;
  on failure, if the value contains `,` and no `.`, retry with `,`→`.`.
- `string StripHeaderAndDetectDialect(string text, out bool isXml)` — return the
  substring from the first `<OFX` onward (drops any `OFXHEADER:`/`DATA:` preamble);
  set `isXml` true if the content is OFX 2.x (`OFXHEADER:200`, `VERSION:2xx`, or a
  leading `<?xml`/`<?OFX`). Encoding is resolved by the importer before this.

### `OfxStatementImporter : IImporter<ImportedStatement>` (Format = Ofx)

`Import(Stream, ImportOptions)`:
1. Read the bytes; pick an encoding — if the raw header contains `CHARSET:1252` or
   `ENCODING:` non-UTF, decode as Windows-1252 (`Encoding.GetEncoding(1252)`); else
   UTF-8. Decode to text.
2. `StripHeaderAndDetectDialect` — if `isXml`, throw `NotSupportedException`
   ("OFX 2.x XML import is not yet supported (slice 4c)."); the endpoint already
   maps a thrown parse error to 422 (see endpoint note).
3. If there is no `<OFX` / no `<STMTRS>` at all → return an empty `ImportResult`
   with a warning ("no bank statement found in the OFX file"). Capture a non-zero
   signon/statement `<CODE>` as a warning too.
4. For each `<STMTRS>` block: read `BANKACCTFROM`→`ACCTID` (AccountHint); the
   `LEDGERBAL` block → `BALAMT` (ClosingBalance) + `DTASOF` (StatementDate); each
   `<STMTTRN>` → `DTPOSTED` (date), `TRNAMT` (signed amount), `FITID` (Reference),
   `NAME`/`MEMO` (Description = `NAME`, else `MEMO`, combined `"NAME — MEMO"` when
   both present and distinct). A transaction with an unparseable date or amount →
   a warning (`"Statement {acct}, transaction {fitid/index}: …"`) and is skipped.
   `OpeningBalance` is left null (OFX carries no opening; the user supplies it).
5. Return `ImportResult<ImportedStatement>(statements, warnings)`.

### Endpoint note (no code change expected)

The 4a endpoint requires `mapping` only for CSV; for `format=ofx` it passes
`ImportOptions { Csv = null }` and resolves the now-registered OFX importer. A
thrown `ArgumentException` already maps to 422; the OFX importer's
"XML-not-supported" path throws `NotSupportedException` — **confirm during
implementation** whether the endpoint catches it (add a `catch (NotSupportedException)
→ 422` if not). That is the only possible endpoint touch.

## Data flow

```
POST /clients/{c}/bank-statements/import?format=ofx   (multipart: file; no mapping)
  → registry.Resolve<ImportedStatement>(Ofx) → OfxStatementImporter
  → decode (CHARSET) → strip header / detect dialect (XML → 422 "4c")
  → per <STMTRS>: ACCTID, LEDGERBAL(BALAMT/DTASOF), each <STMTTRN> → ImportedLine
       bad txn → warning + skip;  opening = null, closing = LEDGERBAL
  → ImportResult<ImportedStatement> (one per STMTRS)
  → ImportPreviewResponse { statements:[{ lines, detectedClosingBalance, statementDate, accountHint }], warnings }
client reviews + supplies opening → POST /bank-statements (Slice 1, foots-or-422)
```

## Error handling

- OFX 2.x XML content → `NotSupportedException` → 422 ("…not yet supported (slice 4c)").
- No `<OFX>`/`<STMTRS>` or a non-zero status `<CODE>` → 200 with an empty/partial
  result + a warning (not an exception) — a malformed-but-readable file shouldn't 500.
- A transaction with a bad date/amount → a warning, the row skipped; the rest parse.
- The endpoint maps a thrown parse exception to 422 (existing behavior; extend to
  `NotSupportedException` if needed).

## Testing

- **`OfxScanner` unit tests:** `Leaf` reads an unclosed leaf up to the next `<`
  (incl. `<SEVERITY>INFO</STATUS>` and a dotted `<INTU.BID>` present-but-ignored);
  `Blocks` returns each `<STMTTRN>`/`<STMTRS>`; `TryParseOfxDate` handles
  `YYYYMMDD`, `YYYYMMDDHHMMSS`, and `…​.LLL[-5:EST]`, and rejects short input;
  `TryParseOfxAmount` handles `-12.34`, `1234.56`, and a comma-decimal `-12,34`;
  `StripHeaderAndDetectDialect` strips a WF-style header, passes a headerless file,
  and flags an `OFXHEADER:200`/`<?xml` file as XML.
- **`OfxStatementImporter` unit tests** (synthetic SGML samples modeling the real
  variety): a WF-style header file → N lines with correct signed amounts/dates/
  references + ClosingBalance from LEDGERBAL (not AVAILBAL) + AccountHint; a
  headerless FRA-style file (with `<SEVERITY>INFO</STATUS>`) → still parses; a
  multiple-`STMTRS` file → `Records.Count == 2`; an empty-tags file (a txn missing
  FITID / empty TRNAMT) → warning + the bad txn skipped, the good ones kept; a
  comma-decimal amount parses; an OFX-2.x-XML input → `NotSupportedException`; an
  error/`signon_fail` file (non-zero CODE, no STMTRS) → empty result + warning.
- **E2E** (Reconciliation host): upload a synthetic OFX 1.x file (multipart,
  `format=ofx`, no mapping) → 200 preview with the expected lines, a
  `DetectedClosingBalance`, and an `AccountHint`; then submit the previewed lines +
  the detected closing (opening computed so it foots) to `POST /bank-statements` →
  201 (round-trip). Plus: an OFX-2.x upload → 422.

## Success criteria

- A real-shape OFX 1.x SGML bank statement parses into `ImportedStatement`(s) with
  correct transactions, the LEDGERBAL closing balance, statement date, and account
  hint — across the header/headerless, single/multiple-statement, and
  empty-tag/locale variants.
- OFX 2.x XML is detected and refused with a clear 4c message (422), not a crash.
- Malformed-but-readable files degrade to warnings, never a 500.
- No framework, CSV, or endpoint behavior changes (beyond a possible
  `NotSupportedException`→422 catch); the Interchange project stays zero-dependency;
  Slices 1-4a and other modules are unchanged.
- New unit + E2E tests green; existing suites stay green.
