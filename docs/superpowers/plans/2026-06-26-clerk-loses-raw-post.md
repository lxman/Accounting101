# Remove Raw GL Write from the Clerk Role — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Change the Clerk role from `{Read, Post, Revise}` to `{Read}` — closing the raw-GL write door so clerks write only through modules — and update the two policy tests that encoded the old matrix, proving (via the existing module E2E suites) that the clerk path is intact.

**Architecture:** One production line (the `RolePermissions` matrix). Two `PolicyTests` methods updated. The "clerk still works through modules" proof is the existing module E2E suites staying green (they seed a Clerk and drive writes through the module credential, which does not consult the user's `Post`).

**Tech Stack:** C#/.NET 10, ASP.NET, xUnit + EphemeralMongo + `WebApplicationFactory<Program>`.

**Spec:** `docs/superpowers/specs/2026-06-26-clerk-loses-raw-post-design.md`

## Global Constraints
- .NET 10; build **0 warnings**; TDD.
- Surgical: the ONLY production change is the one matrix line in `Backend/Accounting101.Ledger.Api/Control/Authorization.cs`. No endpoint/handler/gateway changes (the engine already denies raw `Post`/`Revise` to anyone lacking the permission).
- Run test classes individually (host-boot flakiness). Stage explicit file lists; do NOT commit in a worktree.
- Commit trailer (verbatim): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

---

## Task 1: Clerk = {Read}; flip the policy tests; prove the module path intact

**Files:**
- Modify: `Backend/Accounting101.Ledger.Api/Control/Authorization.cs` (line 45 — the Clerk matrix entry)
- Modify: `Backend/Accounting101.Ledger.Api.Tests/PolicyTests.cs` (two methods)

**Interfaces:**
- `RolePermissions.Map[LedgerRole.Clerk]` — currently `[Permission.Read, Permission.Post, Permission.Revise]`.
- Test helpers (existing): `fixture.SeedClientAsync(role: LedgerRole.Clerk)`, `fixture.AddMemberAsync(clientId, LedgerRole.Controller)`, `PostAsync(http, clientId, cash, revenue, amount)` (raw post helper), `Entry(cash, revenue, amount)`, `ReviseRequest`.

- [ ] **Step 1: Write the failing tests.** In `PolicyTests.cs`, replace `A_clerk_can_post_but_cannot_approve` (currently asserts a clerk CAN post raw) with the denial test, and fix the approver test's setup. New/changed test bodies:
```csharp
[Fact]
public async Task A_clerk_cannot_post_or_revise_raw_entries()
{
    SeededClient c = await fixture.SeedClientAsync(role: LedgerRole.Clerk);
    Guid cash = Guid.NewGuid(), revenue = Guid.NewGuid();

    // Raw post is denied — a clerk writes only through modules now.
    HttpResponseMessage post = await c.Http.PostAsJsonAsync(
        $"/clients/{c.ClientId}/entries", Entry(cash, revenue, 100m));
    Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);

    // Raw revise is denied too (the permission check precedes the entry lookup, so a
    // nonexistent id still yields 403, not 404).
    HttpResponseMessage revise = await c.Http.PostAsJsonAsync(
        $"/clients/{c.ClientId}/entries/{Guid.NewGuid()}/revise",
        new ReviseRequest(null, new DateOnly(2026, 4, 1), null, null, "x",
            [new PostLineRequest(cash, "Debit", 100m), new PostLineRequest(revenue, "Credit", 100m)]));
    Assert.Equal(HttpStatusCode.Forbidden, revise.StatusCode);
}
```
And in `An_approver_can_approve_but_cannot_post`, change the entry-poster from a Clerk to a Controller (a Clerk can no longer post raw):
```csharp
    // A controller posts (clerks no longer post raw); the approver approves.
    HttpClient poster = await fixture.AddMemberAsync(c.ClientId, LedgerRole.Controller);
    Guid id = await PostAsync(poster, c.ClientId, cash, revenue, 100m);
    HttpResponseMessage approve = await c.Http.PostAsync($"/clients/{c.ClientId}/entries/{id}/approve", null);
    Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
```
(Confirm the exact `ReviseRequest` constructor arity against the existing `With_sod_the_reviser_cannot_approve_their_own_revision` test at PolicyTests.cs:66 — mirror it. Keep its first arg whatever that test passes, e.g. `null`.)

- [ ] **Step 2: Run, confirm fail.** `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter PolicyTests` → `A_clerk_cannot_post_or_revise_raw_entries` FAILS (clerk still has Post/Revise → post returns 201, not 403). Expected red.

- [ ] **Step 3: Implement** the matrix change in `Authorization.cs`:
```csharp
        [LedgerRole.Clerk] = [Permission.Read],
```

- [ ] **Step 4: Run, confirm pass + prove the module path intact.**
  - `dotnet test Backend/Accounting101.Ledger.Api.Tests --filter PolicyTests` → all green (clerk denied raw post+revise; approver flow via Controller; SoD/auditor/reverse unchanged).
  - **Regression = the positive proof** (run each individually): these seed a **Clerk** and drive writes through the module credential — they MUST stay green, proving a `{Read}`-only clerk still works:
    - `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter ModuleViaReceivablesTests`
    - `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter CashApplicationTests`
    - `dotnet test Modules/Receivables/Accounting101.Receivables.Tests --filter ReceivablesDispositionsE2eTests`
    - `dotnet test Modules/Payables/Accounting101.Payables.Tests --filter ModuleViaPayablesTests`
    - one Payroll E2E class and one Cash E2E class (the module record-and-post path as a Clerk)
  - If ANY module E2E fails, STOP and report — it means that path secretly relied on the user's `Post` (a real finding).
  - Also confirm `AccountTests` + `OnboardingTests` clerk cases stay green (they assert a clerk lacks `ManageAccounts` — unaffected).
  - Full solution builds 0 warnings.

- [ ] **Step 5: Commit** — `feat(ledger): Clerk role loses raw Post/Revise — writes go through modules`.

---

## Final verification
- [ ] `dotnet build Accounting101.slnx -c Debug` → 0 warnings.
- [ ] `PolicyTests` green; module E2E suites (Receivables/Payables/Payroll/Cash, seeding a Clerk) all green; `AccountTests`/`OnboardingTests` clerk cases green.
- [ ] Confirm: a Clerk gets 403 on raw `POST /entries` and raw `/revise`; a Clerk still issues invoices / records receipts / dispositions / bills / payroll / disbursements through modules (viaModule stamped); SoD intact (clerk cannot approve/void/reverse).
- [ ] Whole-branch review on the most capable model (RBAC/auth surface), then `superpowers:finishing-a-development-branch`.

## Self-review (author)
- **Spec coverage:** matrix change (Step 3), the two PolicyTests methods (Step 1), the regression proof (Step 4). Scope boundary (no sim changes — slice 7) respected.
- **Type consistency:** `RolePermissions.Map[LedgerRole.Clerk]` is a `Permission[]`; `ReviseRequest` arity mirrored from the existing revise test; `AddMemberAsync(clientId, LedgerRole.Controller)` matches the helper signature used at PolicyTests.cs:103.
- **Open implementer checks:** (a) verify the `ReviseRequest` constructor signature against PolicyTests.cs:66 before writing the new revise assertion; (b) if any module E2E that seeds a Clerk fails, that path depended on user-`Post` — STOP and report (do not grant the clerk Post back); (c) confirm no OTHER test in the engine suite seeds a Clerk and posts raw (grep `LedgerRole.Clerk` shows only PolicyTests + AccountTests + OnboardingTests in the engine suite — the latter two test ManageAccounts denial, unaffected).
