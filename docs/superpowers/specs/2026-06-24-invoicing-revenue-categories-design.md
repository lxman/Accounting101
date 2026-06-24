# Invoicing: native revenue-account split by category

**Date:** 2026-06-24
**Status:** Approved (design), pending implementation
**Repo:** Accounting101 (product) — `Modules/Accounting101.Invoicing*`. Simulation fallout lives in `Accounting101.Simulation`.

## Problem

The invoicing recipe credits **all** invoice revenue to a single `RevenueAccountId`
(Consulting Revenue, 4000). When an invoice carries software-license revenue (which belongs in
4100, Software License Revenue), the recipe still dumps it on 4000. The Summit Consulting
simulation works around this by having the AR clerk hand-post a reclassification journal entry
(`Dr 4000 / Cr 4100`) after every license-bearing invoice. That workaround is the single
most-repeated piece of friction in the dog-food run, and it is a real product gap: the module
cannot classify revenue by what was sold.

## Goal

Let the module split invoice revenue across multiple revenue accounts natively at issue time, so
no downstream reclass entry is ever needed. Keep the change aligned with the codebase's grain:
the commercial line carries a **semantic key**, account ids are resolved at the **config
boundary**, and the journal entry stays balanced by construction.

## Non-goals

- No product/item catalog. Lines carry a category string, not a catalog reference.
- No multi-jurisdiction tax change. Tax stays a single line against one Sales Tax Payable account.
- No per-client chart discovery of posting accounts. The configured-accounts model is unchanged
  in shape; it only gains a category map.

## Design (chosen approach: semantic category + config map)

Mirrors how dimensions already work: a line states *what was sold* (a semantic key); config maps
that key to a chart account at setup. The line never carries a Guid.

### Domain model

- **`InvoiceLine`** gains `string? RevenueCategory` (nullable; default `null`).
  - `null` means "use the default revenue account."
  - `Taxable` is untouched and remains purely about tax — orthogonal to revenue classification.
- **`InvoicePostingAccounts`**: replace `RevenueAccountId` with:
  - `Guid DefaultRevenueAccountId` — fallback when a line's category is null or unmapped.
  - `IReadOnlyDictionary<string, Guid> RevenueAccountsByCategory` — category → account id,
    resolved from config at setup (alongside the existing three accounts). May be empty
    (then every line resolves to the default — byte-identical to today's single-revenue behavior).

### Recipe (`InvoicePosting.Compose`)

1. A/R debit line: unchanged (`Dr Receivable = invoice.Total`, tagged with the Customer dimension).
2. Revenue credits: group `invoice.Lines` by **resolved revenue account**
   `RevenueAccountsByCategory.GetValueOrDefault(line.RevenueCategory ?? "") ?? DefaultRevenueAccountId`,
   emit one `Cr` per distinct account for that group's summed line `Amount`.
   - Deterministic order: group credits by ascending account Guid (stable, test-friendly).
   - Each credit is the exact decimal sum of its group's line amounts, so the revenue credits sum
     to `invoice.Subtotal` with **zero rounding residue**.
3. Tax credit line: unchanged (`Cr SalesTaxPayable = invoice.Tax`), only when `Tax != 0`.

Balance holds by construction: `Dr Total = Σ(group subtotals) + Tax = Subtotal + Tax = Total`.
`Compose` stays pure (request in, wire DTO out).

### Web tier

- **`DraftInvoiceRequest`** needs no structural change — it reuses the domain `InvoiceLine`, so the
  new optional `RevenueCategory` flows through automatically (JSON omits it → `null`).
- **`ConfiguredInvoiceAccountsProvider`** reads:
  - `Invoicing:Accounts:Revenue` → `DefaultRevenueAccountId` (key name retained for back-compat).
  - `Invoicing:Accounts:RevenueByCategory:<Category>` → entries of `RevenueAccountsByCategory`
    (bind the whole `RevenueByCategory` section; absent section → empty map).

### Simulation fallout (the payoff — separate concern within slice 2)

- `TransactionGenerator` tags license invoice lines with `RevenueCategory = "License"` so the
  split happens at issue.
- `EngineStack` sets `Invoicing__Accounts__RevenueByCategory__License` to `ChartIds.For("4100")`.
- The AR clerk brief's **reclass-workaround** section is deleted; its account-reference notes for
  4000/4100 are updated to reflect the native split.
- Re-run the 3-month harness; reconciliation must stay at 0/0/0/0 with **no** RECLASS entries in
  the journal.

## Testing (TDD)

Pure-unit tests on `Compose` (drive first):
- Single default category (no category set) → one revenue credit on the default account (today's behavior).
- Two categories mapped to two accounts → two revenue credits, each the correct line-sum; entry balances.
- Category present but unmapped → folds into the default account.
- Mixed mapped + null lines → null lines fold into default; mapped lines split out.
- Tax present / tax absent → tax line emitted only when non-zero; revenue split unaffected.
- Rounding: category credits sum exactly to subtotal; total debit equals sum of all credits.

End-to-end (through the real host, EphemeralMongo):
- Issue a license-bearing invoice with `RevenueCategory: "License"` configured → journal shows
  `Cr 4000` (consulting) and `Cr 4100` (license) directly, no reclass entry; A/R subledger ties out.

## Risks / mitigations

- **Breaking `InvoicePostingAccounts` consumers** (rename `RevenueAccountId` → `DefaultRevenueAccountId`):
  compile-time caught; only the recipe, the provider, and tests reference it.
- **Config drift** (category key not configured): unmapped category silently falls back to default —
  acceptable and intentional (a plain invoice must always post). No throw on unmapped category.
