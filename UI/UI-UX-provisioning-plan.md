# Accounting 101 — UI, UX & Provisioning Plan

## Overview

Architecture blueprint for standing up a multi-tenant demo website at
`accounting-101.com` — a modular double-entry accounting engine backed by
.NET 10 + MongoDB, fronted by a modern SPA with Entra ID authentication.
Users are provisioned manually by the firm. Each user controls one or more
bookkeeping clients under their firm profile.

---

## Domain Architecture

```
accounting-101.com/
  ├── accounting-101.com           →  Azure Static Web Apps (free)
  │     SPA serving all user-facing routes, no server-rendering.
  │
  └── api.accounting-101.com       →  Azure App Service B1 (~$13/mo)
        The existing .NET LedgerEngine host. Never navigated by the user —
        called as a JSON API behind the scenes by the SPA.
```

**Key rule**: the user's address bar never leaves `accounting-101.com`. The API
subdomain is an implementation detail.

---

## Hosting Stack

| Layer               | Azure Service           | Cost     | Notes                                 |
|---------------------|-------------------------|----------|---------------------------------------|
| Frontend (SPA)      | Static Web Apps         | Free     | 100 GB bandwidth, auto-TLS, CDN       |
| Backend (API)       | App Service B1          | ~$13/mo  | 1.75 GB RAM, 1 core — plenty for demo |
| Database            | MongoDB Atlas M0        | Free     | 512 MB shared — one DB per client     |
| Auth                | Entra ID                | Free     | Free for up to 50K users in a tenant  |
| Domain              | accounting-101.com (Squarespace DNS) | Paid  | Apex ALIAS → Static Web Apps          |

**Total recurring: ~$13/mo.**

### Domain DNS Setup (Squarespace Domains)

| Record  | Host       | Target                                           |
|---------|------------|--------------------------------------------------|
| ALIAS   | @          | `your-static-app.azureedge.net`                  |
| CNAME   | api        | `your-app-service.azurewebsites.net`             |
| TXT     | @          | Verification code from Azure (domain ownership)  |

- Azure provisions and auto-renews SSL certificates for both domains — no
  separate cert purchase, no Route53 or Cloud DNS service needed.
- Apex `accounting-101.com` is handled via Squarespace's ALIAS record support
  (inherited from Google Domains).

---

## Authentication (Entra ID)

The API already consumes `ClaimsPrincipal` per request via `LedgerGateway`.
Entra ID integration adds:

```csharp
// In Program.cs — ~20 lines
builder.Services.AddAuthentication()
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}";
        options.Audience = "api://accounting-101";
    });
```

The SPA uses MSAL.js (`@azure/msal-browser`) to acquire tokens:

```
1. User hits accounting-101.com → SPA loads → sees "Sign in with Microsoft"
2. Click → redirect to login.microsoftonline.com → consent → redirect back
3. SPA has JWT → all fetch() calls to api.accounting-101.com include
   Authorization: Bearer <token>
4. LedgerGateway.ResolveAsync maps the Entra oid claim → internal UserId →
   ClientMembership → LedgerContext
```

### New Backend Component: User Mapping Store

A new collection in the control MongoDB (`user_mappings` or equivalent) mapping:

```
EntraOid (string) → InternalUserId (Guid), DisplayName, Email
```

The existing `ControlStore.GetMembershipAsync(internalUserId, clientId)`
already resolves role + permissions. No structural changes to the permission
model.

---

## Provisioning (Manual — By The Firm)

No self-service signup. The firm provisions each user manually:

```
Person emails / expresses interest
         │
         ▼
Firm admin runs provisioning script:
         │
         ├── 1. Create Entra user (or invite as guest)
         ├── 2. Insert user_mapping row (EntraOid → InternalUserId)
         ├── 3. Create ClientRecord in control DB (name, FY end, address)
         ├── 4. Seed standard chart of accounts
         ├── 5. Seed demo data (a customer, vendor, opening balances)
         └── 6. Insert membership (InternalUserId → ClientId → Controller role)
         │
         ▼
Firm emails person: "Go to accounting-101.com and sign in with your
                     Microsoft work/school account. Your demo company
                     is ready."
```

The provisioning script is a standalone CLI:

```
dotnet run provision --email user@example.com --company "Acme Corp"
```

It calls the engine's existing `/clients/{id}/onboarding` and
`PUT /clients/{id}/accounts` endpoints, plus the new provisioning-only
endpoints for user mapping and client metadata.

> **Security boundary**: provisioning endpoints are admin-only (or CLI-only —
> no public route). Normal users cannot create clients or grant access.

---

## User Journey & Screen Map

### Step 1: Login

```
accounting-101.com/  →  Landing page with "Sign in with Microsoft" button
accounting-101.com/login  →  (MSAL redirect to Microsoft, returns to dashboard)
```

### Step 2: Business Setup (first-time / no business profile)

```
accounting-101.com/setup/business

  Fields: Business Name, Address, Phone, EIN/ITIN/SSN (maybe excluded for demo)
  Action: Save → creates business profile record → redirect to client setup
```

> **Backend**: New `BusinessProfile` collection (control DB or separate).
> CRUD endpoints needed (none exist today). Business is the firm itself
> (the accounting firm / bookkeeping practice), not a client.

### Step 3: Client Setup (first-time / no clients)

```
accounting-101.com/setup/client

  Fields: Client Name, Address, Fiscal Year End, Contact Info
  Action: Save → creates ClientRecord + seeds Chart of Accounts →
          redirect to dashboard
```

> **Backend**: Extends the existing `ClientRegistration` (or a new
> `ClientProfile` collection) with name, address, FY end. The engine's
> `/onboarding` endpoint already seeds opening balances — the chart seeding
> is a separate concern that runs at this point.

### Step 4: Dashboard (post-setup)

```
accounting-101.com/dashboard

  ┌─────────────────────────────────────────┐
  │  Client Switcher:  [Acme Corp ▼]        │
  ├─────────────────────────────────────────┤
  │  Quick Actions:                         │
  │    ├── Post Journal Entry               │
  │    ├── Trial Balance                    │
  │    ├── Income Statement                 │
  │    └── Period Close                     │
  ├─────────────────────────────────────────┤
  │  Sidebar / Nav:                         │
  │    Firm Setup                           │
  │      ├── Business Profile               │
  │      └── Manage Users (future)          │
  │    Clients                              │
  │      ├── New Client                     │
  │      └── Edit Client List               │
  │    Accounting                           │
  │      ├── Journal                        │
  │      ├── Accounts (Chart)               │
  │      ├── Trial Balance                  │
  │      ├── Statements                     │
  │      │   ├── Balance Sheet              │
  │      │   ├── Income Statement           │
  │      │   └── Cash Flow                  │
  │      └── Periods                        │
  │          ├── Close Month                │
  │          ├── Close Year                 │
  │          └── Reopen Period              │
  │    Subledgers                           │
  │      ├── Receivables                    │
  │      ├── Payables                       │
  │      ├── Cash                           │
  │      └── Payroll                        │
  └─────────────────────────────────────────┘
```

### Progressive Menu Reveal

The sidebar/nav reveals sections as the user completes setup milestones:

| State | Visible Menu Items |
|-------|-------------------|
| Logged in, no business profile | Setup → Business Info |
| Business saved, no clients | Setup → Business Info, Clients → New Client |
| First client created | Setup → Business Info, Clients → New Client / Client List, **Accounting** (all), **Subledgers** (all) |
| Multiple clients | Same + **Client Switcher** at top of every page |

---

## What Exists vs. What Needs Building

### Already Exists (Backend Engine)

| Endpoint / Feature | Route | Notes |
|-------------------|-------|-------|
| Post journal entry | `POST /clients/{id}/entries` | Lands PendingApproval |
| Validate entry | `POST /clients/{id}/entries/validate` | Dry-run pre-flight |
| Approve entry | `POST /clients/{id}/entries/{id}/approve` | Puts on books |
| Void entry | `POST /clients/{id}/entries/{id}/void` | Withdraw or reverse |
| Revise entry | `POST /clients/{id}/entries/{id}/revise` | Correction (supersede path) |
| Reverse entry | `POST /clients/{id}/entries/{id}/reverse` | Closed-period-safe correction |
| Close period | `POST /clients/{id}/periods/close` | Snapshot + freeze |
| Close year | `POST /clients/{id}/periods/close-year` | Closing entry → retained earnings |
| Reopen period | `POST /clients/{id}/periods/reopen` | Admin/step-up gated |
| Upsert account | `PUT /clients/{id}/accounts/{id}` | Chart of accounts CRUD |
| Onboarding | `POST /clients/{id}/onboarding` | Opening balances |
| List entries | `GET /clients/{id}/entries` | Filtered + paginated |
| Get entry | `GET /clients/{id}/entries/{id}` | |
| Trial balance | `GET /clients/{id}/trial-balance` | Optional `?asOf=` |
| Subledger | `GET /clients/{id}/subledger` | By dimension + account |
| Subledger reconciliation | `GET /clients/{id}/subledger/reconciliation` | Control account tie-out |
| Balance sheet | `GET /clients/{id}/statements/balance-sheet` | |
| Income statement | `GET /clients/{id}/statements/income-statement` | |
| Cash flow | `GET /clients/{id}/statements/cash-flow` | |
| List accounts | `GET /clients/{id}/accounts` | |
| Get account | `GET /clients/{id}/accounts/{id}` | |
| Account balance | `GET /clients/{id}/accounts/{id}/balance` | |
| Audit log | `GET /clients/{id}/audit` | |
| Audit verify | `GET /clients/{id}/audit/verify` | Tamper-evident chain check |
| Entry audit | `GET /clients/{id}/audit/{id}` | |

### Exists (Backend Modules — Evidentiary Docs + Posting)

| Module | Documents | Key Service Methods |
|--------|-----------|-------------------|
| Receivables | invoices, payments, credit-applications, write-offs, credit-notes, refunds | CreateCustomer, Draft/Issue/Void Invoice, RecordPayment, RecordCreditApplication, RecordWriteOff, RecordCreditNote, RecordRefund + void variants |
| Payables | bills, bill-payments | Draft/Issue/Void Bill, RecordPayment, void variants |
| Payroll | payroll-runs, tax-remittances | RecordRun, VoidRun, RecordRemittance, VoidRemittance |
| Cash | cash-disbursements, cash-deposits | RecordDisbursement, VoidDisbursement, RecordDeposit, VoidDeposit |

### Needs Building (Backend)

| Component | Priority | Notes |
|-----------|----------|-------|
| **Entra JWT auth wiring** | P0 | ~20 lines in Program.cs. Add `Microsoft.AspNetCore.Authentication.JwtBearer` package. Configure authority + audience. |
| **User mapping store** | P0 | New collection: EntraOid → InternalUserId, DisplayName, Email. Lookup endpoint for LedgerGateway (or inline in middleware). |
| **Business profile CRUD** | P1 | New collection + endpoints: name, address, phone, EIN. Called by SPA setup wizard. |
| **Client metadata CRUD** | P1 | Extend `ClientRegistration` or new collection: name, address, FY end, contact info. Needed by setup wizard and client switcher. |
| **Chart-of-accounts seeding endpoint** | P1 | A convenience endpoint or script that creates a standard COA template for a new client. Uses existing `UpsertAccount`. |
| **Provisioning CLI** | P2 | `dotnet run provision --email X --company Y`. Orchestrates: create Entra user → insert mappings → create client → seed COA → seed demo data → assign role. Calls internal endpoints; not a public route. |

### Needs Building (Frontend — SPA)

| Page / Component | Priority | Backed By |
|-----------------|----------|-----------|
| Landing page (accounting-101.com/) | P0 | Static HTML + MSAL.js |
| Login flow (MSAL redirect) | P0 | MSAL.js `loginRedirect()` |
| Business setup form | P1 | New BusinessProfile endpoints |
| Client setup form | P1 | New ClientProfile endpoints |
| Dashboard layout | P0 | Own design |
| Client switcher | P1 | ClientProfile list endpoint |
| Journal entry form | P1 | POST /entries + GET /accounts |
| Entry approval list | P1 | GET /entries (?posting=PendingApproval) |
| Trial balance view | P1 | GET /trial-balance |
| Balance sheet view | P1 | GET /statements/balance-sheet |
| Income statement view | P1 | GET /statements/income-statement |
| Cash flow statement view | P2 | GET /statements/cash-flow |
| Chart of accounts editor | P2 | PUT /accounts/{id} + GET /accounts |
| Period close UI | P2 | POST /periods/close |
| Subledger views | P2 | GET /subledger |
| Audit log viewer | P3 | GET /audit |

---

## Recommended SPA Framework

SvelteKit + Skeleton UI (Tailwind component library).

**Rationale:**

- Tiny bundle size (~2 KB runtime) — fast load on demo connections
- Excellent reactivity for data-entry forms (invoice lines, journal entry grids)
- Skeleton provides polished components out of the box (tables, forms, nav,
  modals) — no hand-rolling UI widgets for a demo
- Static adapter outputs to `dist/` → deploy to Azure Static Web Apps with
  zero server infrastructure

Alternative: vanilla HTML + HTMX or plain JS + MSAL.js for the fastest possible
path to clickable.

---

## Demo Walkthrough Script

What a new user sees when they first log in:

1. **Landing** — clean login page. "Sign in with Microsoft."
2. **Business Setup** — "Tell us about your firm." Name, address, phone. Save.
3. **Client Setup** — "Create your first client." Name, FY end. Save.
4. **Chart of Accounts** — seeded automatically. User can view or edit.
5. **Dashboard** — full menu appears. Client switcher shows their new client.
6. **Post an entry** — "Record a journal entry." Dr Cash 1000 / Cr Revenue 1000.
7. **Approve** — navigate to pending entries, approve it.
8. **Trial Balance** — see the entry hit the books.
9. **Statements** — balance sheet + income statement show the transaction.
10. **Create a second client** — switch in the client switcher, set up another.
11. **Period Close** — close the month, confirm freeze works.
12. **Reversal** — back-date a correction, see it land in the open period.

---

## Future Considerations (Post-Demo)

- Multi-user roles within a single firm (admin, clerk, approver per client)
- EIN/SSN fields with encryption-at-rest
- Bank reconciliation module
- Reporting exports (PDF, CSV)
- Bulk import of opening balances from spreadsheet
- Automated backups and point-in-time restore
