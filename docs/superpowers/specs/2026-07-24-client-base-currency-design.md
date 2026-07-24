# Client base currency — Design

**Date:** 2026-07-24
**Status:** Spec for review
**Context:** Accounting101 has no currency modeling. `BillLine.Amount`, `Bill.Total`, ledger
`Line`, and `PostEntryRequest` are all bare `decimal`; the 53 "currency"-adjacent hits in the
codebase are string interpolation and `CultureInfo.InvariantCulture` used only for parsing
CSV/OFX. The ledger is structurally single-currency, and there is not even a field to *record*
that an amount is denominated in EUR rather than USD. This blocks the e-invoicing inbound module
(a separate, later sub-project): Factur-X / ZUGFeRD is a European standard, so most real inbound
documents carry `BT-5 = EUR`, and there is no honest way to land them. Rather than hardcode a
USD-only gate, we give the platform a **base-currency setting**: declare which single currency the
books are kept in. This is a label and a formatting/validation hint — **not** a foreign-exchange
feature. No exchange rates, no per-entry currency, no multi-currency. A EUR-based firm then keeps
EUR books natively, and inbound e-invoices are gated on "invoice currency must equal the client's
base currency."

## Principle

Base currency is **client metadata, not a journal value.** It respects the two standing rules of
this codebase:

1. **Only journal-derived numbers are values; classification/labels live elsewhere.** Amounts on
   the journal stay bare `decimal`. The engine never learns about currency. Base currency is a
   per-client attribute in the control DB, exactly like `ApprovalMode` and `FiscalYearEndMonth`.
2. **The engine enforces only irreducible invariants; policy lives upstream.** Base currency is
   host policy stored in the control DB. The engine's balance/freeze/concurrency invariants are
   untouched.

**Set once, at provisioning, immutable thereafter.** Once any entry exists, the base currency *is*
the denomination of every historical amount. Changing it would silently re-denominate all history
without an FX conversion event — nonsense. So it behaves like `DatabaseName` (fixed at creation),
not like `ApprovalMode` (mutable policy). Immutability is **structural**: there is no setter
contract and no update endpoint. The create-client handler is the sole writer.

## Approach

### Data model — one field on `ClientRegistration`

`Backend/Accounting101.Ledger.Api/Control/ClientRegistration.cs` gains:

```csharp
/// <summary>The ISO 4217 code of the single currency this client's books are kept in (e.g. "USD",
/// "EUR", "GBP"). Set once at provisioning and immutable thereafter — changing it would
/// re-denominate all history. Host policy stored in the control DB, like ApprovalMode; the engine
/// never sees it. A legacy document stored before this field existed deserializes to "USD".</summary>
public string BaseCurrency { get; set; } = "USD";
```

The `= "USD"` default covers both omission at creation and legacy documents (same legacy-safe-
default pattern as `FiscalYearEndMonth = 12` / `Status = Active`).

### ISO 4217 table — the allowlist and symbol source (one source of truth)

A static table of **real circulating currencies** — the ISO 4217 currency codes, excluding the
X-codes (no-currency `XXX`, testing `XTS`, precious metals `XAU/XAG/XPT/XPD`, and the fund/bond
codes). Each row: `Code` (e.g. `"EUR"`), `Symbol` (e.g. `"€"`), `Name` (e.g. `"Euro"`).

Lives in `Accounting101.Ledger.Contracts` (zero-dep boundary; reachable by the Api handler that
validates and by any consumer that needs the catalog). Shape:

```csharp
public sealed record CurrencyInfo(string Code, string Symbol, string Name);

public static class Iso4217Currencies
{
    public static IReadOnlyList<CurrencyInfo> All { get; } = [ /* ~150 real currencies */ ];
    public static bool IsKnown(string code) => /* case-insensitive lookup */;
    public static CurrencyInfo? Find(string code) => /* or null */;
}
```

Currencies with no well-known single-glyph symbol carry the code itself as `Symbol` (e.g.
`CHF → "CHF"`), so display always has something to show. This is the **only** symbol source; the
frontend does not maintain a parallel map.

### Provisioning — set-once wiring

`CreateClientRequest` (`AdminContracts.cs`) gains:

```csharp
/// <summary>ISO 4217 code of the books' base currency. Omitted → "USD". Validated against the
/// ISO 4217 currency allowlist; unknown/ineligible codes are rejected (422). Immutable after
/// provisioning — there is no setter.</summary>
public string? BaseCurrency { get; init; }
```

The create-client handler:
- Normalizes to upper-case; `null`/empty → `"USD"`.
- Validates via `Iso4217Currencies.IsKnown`; an unknown or X-code → **422**, mirroring how
  `ApprovalMode.Unspecified` is rejected today.
- Writes it onto the new `ClientRegistration`. This is the sole write site.

`ClientRegistrationResponse` gains `string BaseCurrency` so the setup flow echoes back the stored
value.

### Immutability enforcement (belt-and-suspenders)

- **No `SetBaseCurrencyRequest`, no update endpoint.** Its deliberate absence — contrast
  `SetFiscalYearEndRequest` / `SetApprovalPolicyRequest`, which exist *because* those are mutable —
  is the guarantee.
- **Store updates stay field-scoped.** Verify during implementation that `ControlStore`'s
  registration mutations (approval mode, fiscal year, status, modules) are field-scoped `$set`
  updates, not full-document replaces that could carry a stale/absent `BaseCurrency` back over the
  stored value. If any path does a full replace, it must preserve `BaseCurrency`. Make this a
  test (below).

### Read / display

- **`CapabilitiesResponse`** (`AdminContracts.cs`) gains `string BaseCurrency`. The frontend
  already fetches this per-client to drive nav filtering and approval UX; it is the natural channel
  for the app to learn how to label amounts. The frontend resolves the symbol from the currency
  catalog (below).
- **`GET /admin/currencies`** → the `Iso4217Currencies.All` catalog (code, symbol, name), for the
  creation dropdown. Mirrors `CapabilityCatalogResponse` ("backend truth for the admin editor").

### UI

- **Client-creation screen:** a base-currency dropdown, populated from `GET /admin/currencies`,
  defaulting to USD. Part of the one-time provisioning form.
- **Client settings:** base currency shown **read-only**, with a "set at setup, cannot be changed"
  note.
- **Amount display:** amounts render with the client's currency **symbol** (from
  `CapabilitiesResponse.BaseCurrency` → catalog lookup). **Scope guard:** symbol only. No
  per-locale number formatting (decimal/grouping separators, symbol placement conventions) — that
  is deliberately out of scope. Numbers keep their current formatting; only the currency glyph
  changes.

## Effect on the inbound e-invoicing module (the downstream consumer)

The paid `Accounting101.EInvoicing` module (separate private repo, its own later brainstorm) will
read the client's `BaseCurrency` and gate inbound documents on `BT-5 == BaseCurrency`, emitting a
blocking finding on mismatch. That module is **out of scope here** — this spec only lands the
capability it depends on. Nothing in this spec references e-invoicing types.

## Testing

Control-plane / API tests (the natural home is the existing client-registration test suites —
`ClientRegistrationTenancyFieldsTests`, `ApiFixture`-based admin tests; real EphemeralMongo):

- **Provisioning persists the code:** create a client with `BaseCurrency = "EUR"` → the stored
  `ClientRegistration.BaseCurrency` is `"EUR"`; `ClientRegistrationResponse` echoes `"EUR"`.
- **Omission defaults to USD:** create with `BaseCurrency = null` → stored `"USD"`.
- **Case normalization:** `"eur"` → stored `"EUR"`.
- **Unknown / ineligible code rejected:** `"XXX"`, `"XAU"`, `"ZZZ"`, `""`-with-whitespace,
  `"US"` → **422**; no client created.
- **Legacy document → USD:** a `ClientRegistration` BSON doc without the field deserializes to
  `BaseCurrency = "USD"`.
- **Immutability — no mutation path:** there is no endpoint/contract that changes `BaseCurrency`;
  and mutating another field (e.g. `SetApprovalPolicy`, `SetFiscalYearEnd`, `SetClientModules`,
  archive) leaves `BaseCurrency` unchanged (the field-scoped-update guarantee).
- **`CapabilitiesResponse` carries it:** `GET …/capabilities` for a EUR client returns
  `BaseCurrency = "EUR"`.
- **Catalog endpoint:** `GET /admin/currencies` returns a non-empty list including USD/EUR/GBP,
  each with a non-empty symbol, and **excludes** the X-codes.

Contracts unit tests (`Iso4217Currencies`):

- `IsKnown` is case-insensitive and true for a sample of real codes, false for X-codes and junk.
- Every row has a non-empty `Symbol` (code fallback where no glyph).

Frontend: creation-form dropdown populated from the catalog; settings shows the code read-only;
an amount renders with the expected symbol. (Depth per the repo's existing frontend-test practice.)

## Scope

**In scope:** the `BaseCurrency` field on `ClientRegistration`; the `Iso4217Currencies` table;
`CreateClientRequest` / `ClientRegistrationResponse` / `CapabilitiesResponse` additions; the
create-handler validation; `GET /admin/currencies`; the creation dropdown, read-only settings
display, and symbol-based amount rendering; the tests above.

**Out of scope (documented):**
- **Any FX / multi-currency** — no exchange rates, no per-entry currency, no conversion. The
  ledger stays single-currency; this only names that currency.
- **Per-locale number formatting** — symbol only; decimal/grouping/placement conventions unchanged.
- **Mutating base currency after provisioning** — structurally disallowed; a future
  re-denomination feature (if ever) would be an explicit FX conversion event, not an edit.
- **The e-invoicing inbound module** — its own private-repo sub-project and brainstorm; this spec
  is its prerequisite only.

## Global constraints

- .NET 10; latest NuGets; namespaces follow folder structure; tests use EphemeralMongo; build 0
  warnings; commit per slice; TDD.
- Engine stays currency-agnostic — no journal, ledger-core, or engine-invariant change. Base
  currency is host policy in the control DB only.
- Stage explicit file lists per commit; watch for Rider `var`-rewrite churn.
