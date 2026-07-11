# OFX 2.x XML Import — Design

*2026-07-11. Closes the last "(d)" fast-follow: `OfxStatementImporter` parses OFX 1.x SGML but refuses OFX 2.x XML with a `NotSupportedException` (slice 4c placeholder). This adds the 2.x XML path by sharing one statement-assembly routine across both dialects behind a small navigator abstraction.*

## Goal

An uploaded OFX 2.x XML bank statement imports into the same `ImportedStatement`/`ImportedLine` shape as OFX 1.x, with the same tolerance (warn-and-skip bad transactions, degrade malformed input to warnings). No new format, endpoint, UI, or engine surface — 2.x is still `InterchangeFormat.Ofx`, and the reconciliation import path already routes OFX through this importer.

## Why a shared abstraction

OFX 2.x uses the **identical element names** as 1.x (`STMTRS`, `STMTTRN`, `DTPOSTED`, `TRNAMT`, `FITID`, `NAME`, `MEMO`, `LEDGERBAL`, `BALAMT`, `DTASOF`, `ACCTID`); it is merely well-formed XML instead of unclosed-tag SGML. The statement-assembly logic (per `STMTRS` → closing/asOf from `LEDGERBAL`; per `STMTTRN` → date/amount/description, warn-and-skip) and the value parsers (`TryParseOfxDate`, `TryParseOfxAmount`) and formatting (`Describe`, `Clean`) are dialect-agnostic. Only the **extraction primitive** differs (tolerant string scan vs real XML). So we abstract extraction and keep ONE assembly routine — a single source of truth for the field/warn contract.

## Architecture

Backend-only, contained to `Accounting101.Interchange`.

### Component 1 — `IOfxNode` (new)
```csharp
public interface IOfxNode
{
    string? Leaf(string tag);                         // first descendant leaf value (trimmed), or null
    IReadOnlyList<IOfxNode> Blocks(string aggregate);  // descendant aggregate nodes
}
```

### Component 2 — `SgmlOfxNode` (new)
Wraps a scope `string`; delegates to the existing `OfxScanner`:
- `Leaf(tag)` → `OfxScanner.Leaf(scope, tag)`
- `Blocks(agg)` → `OfxScanner.Blocks(scope, agg).Select(s => new SgmlOfxNode(s))`

The 1.x path now flows through this — behaviour-identical (same `OfxScanner` calls, same results), guarded by the existing 1.x tests.

### Component 3 — `XmlOfxNode` (new)
Wraps an `XElement`; matches on **`Name.LocalName`, case-insensitively** — tolerant of the rare namespaced or mixed-case export (OFX 2.x is spec'd unqualified-uppercase, mirroring the SGML path's `OrdinalIgnoreCase`):
- `Leaf(tag)` → first descendant element whose `LocalName` equals `tag` (ignore case) → `.Value.Trim()`, or null.
- `Blocks(agg)` → descendant elements whose `LocalName` equals `agg` → `new XmlOfxNode(e)` each.

Descendant (any-depth) matching mirrors the SGML "first `<tag>` in scope" semantics; scoping is preserved because `AssembleStatements` calls `Leaf`/`Blocks` on the specific node (e.g. `Leaf("BALAMT")` on the `LEDGERBAL` node, not the whole statement).

### Component 4 — `AssembleStatements(IOfxNode root, List<string> warnings)` (extracted)
The *current* inline `STMTRS`/`STMTTRN` loop from `Import`, refactored verbatim to call `node.Leaf`/`node.Blocks` instead of `OfxScanner.Leaf(scope, …)`/`OfxScanner.Blocks(scope, …)`. Includes the existing 0-`STMTRS`→check-`CODE`→warn behaviour. Reuses `TryParseOfxDate`/`TryParseOfxAmount`/`Describe`/`Clean` unchanged.

### Component 5 — `Import` (rewired)
```
string body = OfxScanner.StripHeaderAndDetectDialect(text, out bool isXml);
IOfxNode root;
if (isXml)
{
    try { root = new XmlOfxNode(XDocument.Parse(body).Root!); }
    catch (System.Xml.XmlException ex)
    { return new ImportResult<ImportedStatement>([], [$"OFX 2.x XML could not be parsed: {ex.Message}"]); }
    // XDocument.Parse(body) where body starts at <OFX> is a valid single-root doc; the <?xml?>/<?OFX?> PIs
    // were already stripped by StripHeaderAndDetectDialect. Guard doc.Root null → same warning path.
}
else root = new SgmlOfxNode(body);
return AssembleStatements(root, warnings);
```
The `NotSupportedException` throw is deleted. Class doc updated (drop "OFX 2.x XML is refused (slice 4c)").

## Error handling / tolerance

- Malformed XML → a warning result, never a throw (matches how the 1.x path degrades).
- Bad `DTPOSTED`/`TRNAMT` → warn-and-skip that line; 0 `STMTRS` → check `CODE`, warn — both inherited free from the shared routine.
- Encoding: the `body` string is already decoded up front (UTF-8 default; Latin-1 only if a 1.x `CHARSET:1252` header is present, which 2.x files don't carry). Parsing the decoded UTF-8 body is correct for the OFX 2.x default. A non-UTF-8 2.x file with a declared `<?xml encoding?>` is a rare edge — **deferred** (would need re-reading from bytes via `XmlReader`).

## Testing

- **`OfxStatementImporterTests` — new OFX 2.x cases:** a well-formed 2.x file parses to the **same `ImportedStatement` as its 1.x twin** (parity assertion); multiple `STMTRS`; `LEDGERBAL` closing + `DTASOF`; a bad `DTPOSTED`/`TRNAMT` → warn-and-skip; `NAME`/`MEMO` → `Describe`; `FITID` → `Reference`; malformed XML → warning (not throw); 2.x with 0 `STMTRS` + non-zero status `CODE` → warning.
- **`XmlOfxNode` unit tests:** `Leaf` first-descendant + trim; `Blocks` scoping; case-insensitive + namespace-tolerant `LocalName` match; missing tag → null.
- **Existing 1.x tests (`OfxScannerTests`, `OfxStatementImporterTests`, `InterchangeRegistryTests`) stay green** — the proof the `SgmlOfxNode`/`AssembleStatements` refactor preserved 1.x behaviour exactly.

## Smoke

Backend parser; no new UI or serialization surface. A dev-stack smoke is **optional** — a light end-to-end (upload a real 2.x `.ofx` through the Bank Rec import screen) can be run at merge time, but the unit tests carry the coverage. Decide at merge.

## Out of scope / non-goals

- OFX **export** (writer) — reader only (matches the "read now, write later" stance).
- Honoring a declared non-UTF-8 `<?xml encoding?>` in a 2.x file (rare; deferred).
- Any change to CSV import, the reconciliation domain, the import endpoint, or the UI.
- Investment/credit-card statement types (`CCSTMTRS`, `INVSTMTRS`) — unchanged scope; only bank `STMTRS` as today.

## Execution

**REQUIRED SUB-SKILL:** Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans`. Fresh implementer per task, task review after each, final whole-branch review. Sonnet throughout (contained refactor + parser); opus for the final review.
