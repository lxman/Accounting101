# Committed dev seed — rebuilds the "Demo Co" test client from an EMPTY database via the engine's own
# HTTP API (no direct Mongo writes, no schema guessing). Run AFTER mongod + host are up on :5000.
#
#   pwsh Tools/seed-demo.ps1                         # seeds against http://localhost:5000
#   pwsh Tools/seed-demo.ps1 -ApiBase http://...     # override base URL
#
# What it does (all idempotent-friendly; run against a fresh .localdev/mongo):
#   1. Provisions client "Demo Co" (SoD on) as a deployment admin  -> prints the NEW client id.
#   2. Enables every module for the client.
#   3. Grants the five dev identities their roles (0001 Controller .. 0005 Admin).
#   4. Builds the full chart with FIXED account GUIDs (must match .localdev/start.ps1's module config)
#      and bakes RequiredDimensions into the ledger-first folded control accounts.
#   5. Opens the books (clean — no opening balances).
#
# After running, set  UI/Angular/src/app/core/api/environment.ts  devClientId to the printed client id
# (that file is local-only / uncommitted). The account GUIDs below are the source of truth shared with
# start.ps1; keep them in sync.

param([string]$ApiBase = 'http://localhost:5000')
$ErrorActionPreference = 'Stop'

# ── Dev identities (subs match UI dev-identities.ts) ──────────────────────────
$devAdmin = @{ sub = '00000000-0000-0000-0000-000000000005'; name = 'Dev Admin'
               claims = @(@{ type = 'role'; value = 'Admin' }, @{ type = 'admin'; value = 'true' }) }

function New-DevToken($identity) {
  $json  = $identity | ConvertTo-Json -Compress -Depth 6
  $bytes = [Text.Encoding]::UTF8.GetBytes($json)
  [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
$adminHeaders = @{ Authorization = "DevToken $(New-DevToken $devAdmin)" }

function Api($method, $path, $body) {
  $args = @{ Method = $method; Uri = "$ApiBase$path"; Headers = $adminHeaders }
  if ($null -ne $body) { $args.Body = ($body | ConvertTo-Json -Compress -Depth 8); $args.ContentType = 'application/json' }
  Invoke-RestMethod @args
}

# ── 1. Provision the client (deployment admin; server assigns the id) ─────────
Write-Host 'Provisioning client "Demo Co" ...'
$client = Api POST '/admin/clients' @{ name = 'Demo Co'; requireSegregationOfDuties = $true; fiscalYearEndMonth = 12 }
$cid = $client.id
Write-Host "  client id = $cid"

# ── 2. Enable every module ────────────────────────────────────────────────────
$modules = @('receivables', 'payables', 'payroll', 'fixedassets', 'cash', 'reconciliation', 'inventory')
Api PUT "/admin/clients/$cid/modules" @{ moduleKeys = $modules } | Out-Null
Write-Host "  modules enabled: $($modules -join ', ')"

# ── 3. Grant the five dev identities their roles ──────────────────────────────
$members = @(
  @{ userId = '00000000-0000-0000-0000-000000000001'; role = 'Controller' },
  @{ userId = '00000000-0000-0000-0000-000000000002'; role = 'Approver' },
  @{ userId = '00000000-0000-0000-0000-000000000003'; role = 'Auditor' },
  @{ userId = '00000000-0000-0000-0000-000000000004'; role = 'ArClerk' },
  @{ userId = '00000000-0000-0000-0000-000000000005'; role = 'Admin' }
)
foreach ($m in $members) { Api POST "/admin/clients/$cid/members" $m | Out-Null }
Write-Host "  members granted: $($members.Count)"

# ── 4. Chart of accounts (FIXED GUIDs — shared with start.ps1 module config) ──
# Each: Number, Name, Type, Id, and optional RequiredDimensions / IsRetainedEarnings.
$accounts = @(
  @{ number = '1000'; name = 'Cash';                          type = 'Asset';     id = '0b1fdb58-46f6-4b57-8081-4b4655360ea9' },
  @{ number = '1200'; name = 'Accounts Receivable';           type = 'Asset';     id = 'cfd7d907-840c-433f-b28c-bef0156fbdd3'; rd = @('Customer', 'Invoice') },
  @{ number = '1300'; name = 'Vendor Credits';                type = 'Asset';     id = 'bd75ecf4-4b49-4614-bbd2-ce070cf56893'; rd = @('Vendor') },
  @{ number = '1400'; name = 'Inventory';                     type = 'Asset';     id = '90c8adce-654e-4335-b06b-ce63f18d6491'; rd = @('Item') },
  @{ number = '1500'; name = 'Fixed Assets at Cost';          type = 'Asset';     id = '17d5f1ad-c24f-4497-b884-dd92339424a7' },
  @{ number = '1510'; name = 'Accumulated Depreciation';      type = 'Asset';     id = '3366b925-2d4a-4e90-899f-034633080c46'; rd = @('Asset') },
  @{ number = '2000'; name = 'Accounts Payable';              type = 'Liability'; id = '78dece27-80b0-4167-ac18-c8ec692cb9c3'; rd = @('Vendor', 'Bill') },
  @{ number = '2050'; name = 'GRNI Clearing';                 type = 'Liability'; id = '57a64739-516c-45f5-b554-7fd6cd21b663' },
  @{ number = '2100'; name = 'Sales Tax Payable';             type = 'Liability'; id = '39eba46a-5ff8-41c5-892a-a8e764a12931' },
  @{ number = '2200'; name = 'Customer Credits';              type = 'Liability'; id = '7f1dcb30-42e3-4e2c-a068-c7315996b966'; rd = @('Customer') },
  @{ number = '2300'; name = 'Withholdings Payable';          type = 'Liability'; id = '20c13f4e-dcac-4cde-b81e-973c4402026f' },
  @{ number = '2400'; name = 'Payroll Taxes Payable';         type = 'Liability'; id = '3b919dfa-787a-4d30-b5ef-dfb93ee56973' },
  @{ number = '3000'; name = "Owner's Equity";                type = 'Equity';    id = '30000000-0000-4000-8000-000000000001' },
  @{ number = '3900'; name = 'Retained Earnings';             type = 'Equity';    id = '39000000-0000-4000-8000-000000000001'; re = $true },
  @{ number = '4000'; name = 'Sales Revenue';                 type = 'Revenue';   id = 'd43fd987-3454-4ebf-8fe2-eddfa2258760' },
  @{ number = '4100'; name = 'Sales Returns & Allowances';    type = 'Revenue';   id = '6619b675-3c91-4ef7-bb65-6f50187696bb' },
  @{ number = '4200'; name = 'Gain on Disposal of Assets';    type = 'Revenue';   id = '27e8e646-b7a7-4852-b1ed-c0d77d722803' },
  @{ number = '5000'; name = 'Cost of Goods Sold';            type = 'Expense';   id = '1f9396d7-a723-4d4a-b9c4-7b86008a7a65' },
  @{ number = '5100'; name = 'Inventory Adjustment';          type = 'Expense';   id = '4e4bd773-16e9-4b24-bf26-03bbed3fc069' },
  @{ number = '6000'; name = 'Operating Expense';             type = 'Expense';   id = '60000000-0000-4000-8000-000000000001' },
  @{ number = '6100'; name = 'Bad Debt Expense';              type = 'Expense';   id = '3bc30ebb-7ca5-45ea-846c-9e9d39ca4a00' },
  @{ number = '6300'; name = 'Salaries Expense';              type = 'Expense';   id = '6765b974-0530-4973-a73a-2a0239c4b10b' },
  @{ number = '6400'; name = 'Payroll Tax Expense';           type = 'Expense';   id = '2f7f4c40-cfd2-42b7-9721-50167f4f28fa' },
  @{ number = '6500'; name = 'Depreciation Expense';          type = 'Expense';   id = '8f82cdac-902b-4a38-b3b1-2915a0a5940b' },
  @{ number = '6600'; name = 'Loss on Disposal of Assets';    type = 'Expense';   id = '126e7c95-7238-44d0-ad29-dd1336d5a259' }
)
foreach ($a in $accounts) {
  $body = @{ number = $a.number; name = $a.name; type = $a.type }
  if ($a.rd) { $body.requiredDimensions = $a.rd }
  if ($a.re) { $body.isRetainedEarnings = $true }
  Api PUT "/clients/$cid/accounts/$($a.id)" $body | Out-Null
}
Write-Host "  chart: $($accounts.Count) accounts (RequiredDimensions on AR/AP/Inventory/Accum/credits)"

# ── 5. Open the books — a minimal balanced opening capital injection ──────────
# Onboarding always posts an opening entry, which needs >=2 balanced lines, so "empty" books are seeded
# as a single owner's-capital contribution: Dr Cash 100,000 / Cr Owner's Equity 100,000 (balances signed
# debit-positive). The balance sheet balances immediately; no subledger/transactional history.
$opening = @{
  asOf     = '2026-01-01'
  balances = @(
    @{ accountId = '0b1fdb58-46f6-4b57-8081-4b4655360ea9'; balance = 100000 },   # 1000 Cash (debit +)
    @{ accountId = '30000000-0000-4000-8000-000000000001'; balance = -100000 }   # 3000 Owner's Equity (credit -)
  )
}
Api POST "/clients/$cid/onboarding" $opening | Out-Null
Write-Host '  books opened at 2026-01-01 (opening capital: Cash 100,000 / Owner''s Equity 100,000)'

Write-Host ''
Write-Host '=== SEED COMPLETE ==='
Write-Host "Client id: $cid"
Write-Host "Set UI/Angular/src/app/core/api/environment.ts devClientId to the id above."
