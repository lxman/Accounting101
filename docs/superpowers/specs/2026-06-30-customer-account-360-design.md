# Customer Account (360) — Design

**Date:** 2026-06-30
**Status:** Approved (design)
**Area:** `UI/Angular` (Receivables feature) + one backend aggregate read endpoint (`Modules/Receivables`)

## Goal

Add a read-only **Customer Account** screen (the "customer 360") reached by clicking a customer in the Customers tab. It pulls a single customer's whole AR picture into one place: header balances, AR **aging**, the **open-invoices** list, an AR **running-balance statement** (statement of account), and a separate **credit-activity** ledger. This is the drill-into-one-customer counterpart to the cross-customer tabs.

## Background / current state

- `Customer` is `{ Id, Name, Email? }` — no address/phone (a customer *editor* is a separate, later slice; this screen is read-only).
- Per-customer reads already exist and are the building blocks: `invoices.GetByCustomerAsync`, `payments.GetPaymentsByCustomerAsync`, `GetCreditApplicationsByCustomerAsync`, `GetWriteOffsByCustomerAsync`, `GetCreditNotesByCustomerAsync`, `GetRefundsByCustomerAsync` (all on `IPaymentStore`/`IInvoiceStore`). `PaymentService` already computes the pieces this screen needs:
  - `ListInvoiceViewsAsync` folds payments + credit-apps + write-offs + credit-notes allocations (non-voided) into an `applied` map per invoice → `OpenBalance`/`SettlementStatus`. The AR balance and open-invoices both derive from this.
  - `GetCustomerCreditBalanceAsync` = non-voided payment remainders − credit-applications − refunds.
- Invoices carry `DueDate` (nullable) — the basis for aging.
- **No aging, statement, or aggregate customer-account endpoint exists.** Those are the new pieces.
- `InvoiceView` = `{ Invoice, OpenBalance, SettlementStatus }`. `Invoice.Total` is computed. `Settlement.OpenBalance/Status` are pure.
- The Customers tab (`customer-list`) lists customers; rows are currently not clickable (no detail existed). This screen is that detail.

## Scope

**In:** one aggregate `GET /customers/{customerId}/account` endpoint returning the full view model; the `CustomerAccount` UI screen + route + clickable customer rows; UI model/service; tests.

**Out (deferred):** statement date-range / as-of picker UI (an `asOf` query param exists for correctness/testability but v1 UI always uses today); PDF/print/export; the customer **editor**; drill-through from a statement line to the underlying document; multi-currency.

## Architecture

### 1. Navigation & IA

- Customer rows in `customer-list` become clickable (whole-row: `cursor-pointer hover:bg-muted/50`, role/tabindex, click + Enter) → `router.navigate(['/receivables/customers', id])`.
- Route under `receivables`: `{ path: 'customers/:id', component: CustomerAccount }`. Place it **after** `customers` (the bare list) so it doesn't shadow it.
- Read-only screen with a back link to `/receivables/customers` (a detail view with no Cancel, so the back-link stays — same rationale as `invoice-detail`).

### 2. Backend — `GET /clients/{id}/customers/{customerId}/account?asOf=`

Returns one `CustomerAccountView`, server-computed over all the customer's non-voided documents. `asOf` is an optional `DateOnly` query param defaulting to the server's today (`DateOnly.FromDateTime(DateTime.UtcNow)`) — present so aging is deterministic in tests and so a date picker can be added later. `404` if the customer doesn't exist; same Read authorization as the other module GETs (under the `RequireAuthorization()` `clients` group).

A new `CustomerAccountService` (in `Accounting101.Receivables`, core) assembles the view from the existing stores. The view model (in the core project, like `CreditDocument`):

```csharp
public sealed record CustomerAccountView(
    Customer Customer,
    decimal ArBalance,
    decimal CreditBalance,
    AgingBuckets Aging,
    IReadOnlyList<OpenInvoiceLine> OpenInvoices,
    IReadOnlyList<StatementLine> StatementLines,
    IReadOnlyList<CreditActivityLine> CreditLines);

public sealed record AgingBuckets(decimal Current, decimal D1To30, decimal D31To60, decimal D61To90, decimal D90Plus);

public sealed record OpenInvoiceLine(Guid InvoiceId, string? Number, DateOnly IssueDate, DateOnly? DueDate, decimal OpenBalance, int DaysOverdue);

public sealed record StatementLine(DateOnly Date, string Type, string? Reference, decimal Charge, decimal Payment, decimal Balance);

public sealed record CreditActivityLine(DateOnly Date, string Type, string? Reference, decimal Amount, decimal CreditBalance);
```

**Computations (all exclude voided docs):**

- **AR balance & open invoices:** reuse the `ListInvoiceViewsAsync` fold — for each **Issued** invoice, `applied` = Σ allocations from non-voided payments + credit-apps + write-offs + credit-notes targeting it; `openBalance = OpenBalance(invoice.Total, applied)`. `ArBalance` = Σ openBalance over issued invoices. `OpenInvoices` = issued invoices with `openBalance > 0`, each with `DaysOverdue = max(0, asOf − DueDate)` (0 when `DueDate` is null).
- **Aging:** bucket each open invoice's `openBalance` by `DaysOverdue`: `Current` (≤ 0, i.e. not yet due / no due date), `D1To30` (1–30), `D31To60` (31–60), `D61To90` (61–90), `D90Plus` (≥ 91). Bucket sums equal `ArBalance`.
- **Credit balance:** `GetCustomerCreditBalanceAsync` (existing).
- **AR statement lines** (oldest-first; running AR balance): one **charge** line per Issued invoice (`Date = issueDate`, `Type="Invoice"`, `Reference=number`, `Charge=invoice.Total`, `Payment=0`); one **settlement** line per non-voided payment / credit-note / write-off / credit-application (`Date = doc date`, `Type` ∈ {"Payment","Credit note","Write-off","Credit applied"}, `Payment = Σ` its allocations to this customer's invoices, `Charge=0`). Sort by `Date` ascending, **charges before settlements on the same date**; then accumulate `Balance += Charge − Payment`.
- **Credit-activity lines** (oldest-first; running credit balance): a `+` line per non-voided payment with `Unapplied > 0` (`Type="Overpayment"`, `Amount = +Unapplied`); a `−` line per non-voided credit-application (`Type="Credit applied"`, `Amount = −Applied`); a `−` line per non-voided refund (`Type="Refund"`, `Amount = −Amount`). Sort by `Date` ascending; accumulate `CreditBalance += Amount`. The final running value equals `CreditBalance` in the header.

The aging / statement / credit-activity builders are **pure static functions** (given the already-read docs + `asOf`) so they unit-test without a host.

Registered next to the other `/customers` routes in `MapReceivablesEndpoints`.

### 3. UI model & service

`core/receivables/receivables.ts` — mirror the backend records:
```ts
export interface AgingBuckets { current: number; d1to30: number; d31to60: number; d61to90: number; d90plus: number; }
export interface OpenInvoiceLine { invoiceId: string; number: string | null; issueDate: string; dueDate: string | null; openBalance: number; daysOverdue: number; }
export interface StatementLine { date: string; type: string; reference: string | null; charge: number; payment: number; balance: number; }
export interface CreditActivityLine { date: string; type: string; reference: string | null; amount: number; creditBalance: number; }
export interface CustomerAccountView {
  customer: Customer; arBalance: number; creditBalance: number; aging: AgingBuckets;
  openInvoices: OpenInvoiceLine[]; statementLines: StatementLine[]; creditLines: CreditActivityLine[];
}
```
`core/receivables/receivables.service.ts`:
```ts
getCustomerAccount(customerId: string): Observable<CustomerAccountView>   // GET /customers/{id}/account
```

### 4. `CustomerAccount` screen

`features/receivables/customer-account.ts`. Route `customers/:id` (read `:id` from route params; on load `getCustomerAccount(id)` → `toSignal`). Layout:
- **Header card:** customer name + email; AR balance and credit balance (tabular-nums).
- **Aging strip:** the five buckets as labeled amounts (a "90+" bucket emphasised when non-zero).
- **Two columns:** left = **Open invoices** table (Number · Issued · Due · Open · Days overdue, overdue rows emphasised) then the **Statement of account** table (Date · Type · Ref · Charge · Payment · Balance); right = **Credit activity** strip (Date · Type · Amount(+/−) · Credit balance).
- States: loading, error (`extractProblem`), and graceful empties ("No open invoices.", "No statement activity.", "No credit activity."). Back link to `/receivables/customers`. Reuses `money`/`displayDate`, `HlmTableImports`, `HlmButton`.

## Data flow

1. Receivables → **Customers** → click a customer row → `/receivables/customers/:id`.
2. `CustomerAccount` → `GET /customers/{id}/account` → one `CustomerAccountView` → render header + aging + open invoices + statement + credit activity.
3. All figures are server-computed and reconcile: aging buckets sum to AR balance; statement ends at AR balance; credit ledger ends at credit balance.

## Error handling

- Unknown customer → backend `404`; the screen shows a "Customer not found" message (relayed via `extractProblem`).
- Read failures relayed inline via `extractProblem`.
- `asOf` invalid → backend `400` (the UI never sends a bad one; v1 omits it → today).

## Testing

**Backend** (`Accounting101.Receivables.Tests`): an endpoint test that seeds a customer with a known sequence — issue invoice(s), a partial payment, a credit-note, an overpayment (→ credit), a credit-application, a refund — then asserts: `ArBalance` and the aging buckets (using a fixed `asOf` to make days-overdue deterministic) sum correctly; the statement lines are date-ordered with a correct running AR balance; the credit lines end at `CreditBalance`; voided docs are excluded; `404` for an unknown customer. Pure-function unit tests for the aging bucketer and the statement/credit builders.

**Frontend:** `customer-account.spec.ts` — loads and renders the header balances, the five aging buckets, the open-invoices rows, the statement rows (with running balance), and the credit-activity rows; the empty states; the "not found" path. `customer-list.spec.ts` — clicking a customer row navigates to `/receivables/customers/:id`.

Reuses `money`/`displayDate`, `HlmTableImports`, `extractProblem`, the whole-row-click convention.

## Files touched

- `Modules/Receivables/Accounting101.Receivables/CustomerAccountView.cs` (new — the view-model records, core project).
- `Modules/Receivables/Accounting101.Receivables/CustomerAccountService.cs` (new — assembly + pure builders).
- `Modules/Receivables/Accounting101.Receivables.Api/ReceivablesEndpoints.cs` — `GetCustomerAccount` + route.
- `Modules/Receivables/Accounting101.Receivables.Tests/…` — endpoint + pure-builder tests.
- `UI/Angular/src/app/core/receivables/receivables.ts` — the five interfaces.
- `UI/Angular/src/app/core/receivables/receivables.service.ts` — `getCustomerAccount`.
- `UI/Angular/src/app/features/receivables/customer-account.ts` (+ `.spec.ts`).
- `UI/Angular/src/app/features/receivables/customer-list.ts` (+ `.spec.ts`) — clickable rows.
- `UI/Angular/src/app/app.routes.ts` — `customers/:id` route.
