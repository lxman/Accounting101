import { Routes } from '@angular/router';
import { Dashboard } from './features/dashboard/dashboard';
import { EntryList } from './features/journal/entry-list';
import { EntryForm } from './features/journal/entry-form';
import { TrialBalance } from './features/trial-balance/trial-balance';
import { Statements } from './features/statements/statements';
import { BalanceSheet } from './features/statements/balance-sheet';
import { IncomeStatement } from './features/statements/income-statement';
import { Placeholder } from './features/placeholder/placeholder';
import { ApprovalQueue } from './features/journal/approval-queue';
import { EntryDetail } from './features/journal/entry-detail';
import { ChartOfAccounts } from './features/accounts/chart-of-accounts';
import { AccountEditor } from './features/accounts/account-editor';
import { CustomerList } from './features/receivables/customer-list';
import { InvoiceList } from './features/receivables/invoice-list';
import { InvoiceEditor } from './features/receivables/invoice-editor';
import { InvoiceDetail } from './features/receivables/invoice-detail';
import { PaymentEditor } from './features/receivables/payment-editor';
import { ReceivablesShell } from './features/receivables/receivables-shell';
import { PaymentList } from './features/receivables/payment-list';
import { CreditList } from './features/receivables/credit-list';
import { AdjustmentEditor } from './features/receivables/adjustment-editor';
import { RefundList } from './features/receivables/refund-list';
import { RefundEditor } from './features/receivables/refund-editor';
import { RefundDetail } from './features/receivables/refund-detail';
import { PaymentDetail } from './features/receivables/payment-detail';
import { CreditDetail } from './features/receivables/credit-detail';
import { CustomerAccount } from './features/receivables/customer-account';
import { PayablesShell } from './features/payables/payables-shell';
import { VendorList } from './features/payables/vendor-list';
import { BillList } from './features/payables/bill-list';
import { BillEditor } from './features/payables/bill-editor';
import { BillDetail } from './features/payables/bill-detail';
import { BillPaymentList } from './features/payables/bill-payment-list';
import { BillPaymentDetail } from './features/payables/bill-payment-detail';
import { BillPaymentEditor } from './features/payables/bill-payment-editor';
import { VendorCreditList } from './features/payables/vendor-credit-list';
import { VendorCreditDetail } from './features/payables/vendor-credit-detail';
import { VendorCreditApplyEditor } from './features/payables/vendor-credit-apply-editor';
import { VendorAccount } from './features/payables/vendor-account';
import { PayrollShell } from './features/payroll/payroll-shell';
import { RunList } from './features/payroll/run-list';
import { RunEditor } from './features/payroll/run-editor';
import { RunDetail } from './features/payroll/run-detail';
import { RemittanceList } from './features/payroll/remittance-list';
import { RemittanceEditor } from './features/payroll/remittance-editor';
import { RemittanceDetail } from './features/payroll/remittance-detail';
import { FixedAssetsShell } from './features/fixed-assets/fixed-assets-shell';
import { AssetList } from './features/fixed-assets/asset-list';
import { AssetEditor } from './features/fixed-assets/asset-editor';
import { AssetDetail } from './features/fixed-assets/asset-detail';
import { DisposeEditor } from './features/fixed-assets/dispose-editor';
import { RunList as FaRunList } from './features/fixed-assets/run-list';
import { RunEditor as FaRunEditor } from './features/fixed-assets/run-editor';
import { RunDetail as FaRunDetail } from './features/fixed-assets/run-detail';
import { DisposalList } from './features/fixed-assets/disposal-list';
import { DisposalDetail } from './features/fixed-assets/disposal-detail';
import { MemberList } from './features/admin/member-list';
import { MemberEditor } from './features/admin/member-editor';
import { CapabilitySetList } from './features/admin/capability-set-list';
import { CapabilitySetEditor } from './features/admin/capability-set-editor';
import { ApprovalPolicyScreen } from './features/admin/approval-policy';
import { BankingShell } from './features/banking/banking-shell';
import { CashList } from './features/banking/cash-list';
import { CashVoucherEditor } from './features/banking/cash-voucher-editor';
import { CashVoucherDetail } from './features/banking/cash-voucher-detail';
import { StatementList } from './features/banking/statement-list';
import { StatementImport } from './features/banking/statement-import';
import { StatementEditor } from './features/banking/statement-editor';
import { StatementDetail } from './features/banking/statement-detail';
import { ReconciliationList } from './features/banking/reconciliation-list';
import { ReconciliationWorksheet } from './features/banking/reconciliation-worksheet';
import { ItemList } from './features/inventory/item-list';
import { ItemEditor } from './features/inventory/item-editor';
import { ItemDetail } from './features/inventory/item-detail';
import { MovementEditor } from './features/inventory/movement-editor';
import { MovementDetail } from './features/inventory/movement-detail';
import { AuditTrail } from './features/audit/audit-trail';
import { navLeafPaths } from './layout/nav';
import { canWrite } from './core/capabilities/can.guard';
import { deploymentAdminGuard } from './core/capabilities/deployment-admin.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', component: Dashboard },
  {
    path: 'journal',
    children: [
      { path: '', pathMatch: 'full', component: EntryList },
      { path: 'new', component: EntryForm, canActivate: [canWrite], data: { requiredCapability: 'gl.post', fallback: '/journal' } },
      { path: 'approvals', component: ApprovalQueue },
      { path: ':id', component: EntryDetail },
    ],
  },
  { path: 'trial-balance', component: TrialBalance },
  {
    path: 'statements',
    component: Statements,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'balance-sheet' },
      { path: 'balance-sheet', component: BalanceSheet },
      { path: 'income-statement', component: IncomeStatement },
    ],
  },
  { path: 'accounts', children: [
    { path: '', pathMatch: 'full', component: ChartOfAccounts },
    { path: 'new', component: AccountEditor, canActivate: [canWrite], data: { requiredCapability: 'gl.manageAccounts', fallback: '/accounts' } },
    { path: ':id/edit', component: AccountEditor, canActivate: [canWrite], data: { requiredCapability: 'gl.manageAccounts', fallback: '/accounts' } },
  ] },
  { path: 'receivables', component: ReceivablesShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'invoices' },
    { path: 'invoices', component: InvoiceList },
    { path: 'invoices/new', component: InvoiceEditor, canActivate: [canWrite], data: { requiredCapability: 'ar.write', fallback: '/receivables/invoices' } },
    { path: 'invoices/:id/edit', component: InvoiceEditor, canActivate: [canWrite], data: { requiredCapability: 'ar.write', fallback: '/receivables/invoices' } },
    { path: 'invoices/:id', component: InvoiceDetail },
    { path: 'payments', component: PaymentList },
    { path: 'payments/new', component: PaymentEditor, canActivate: [canWrite], data: { requiredCapability: 'ar.write', fallback: '/receivables/payments' } },
    { path: 'payments/:id', component: PaymentDetail },
    { path: 'customers', component: CustomerList },
    { path: 'customers/:id', component: CustomerAccount },
    { path: 'credits', component: CreditList },
    { path: 'credits/new', component: AdjustmentEditor, canActivate: [canWrite], data: { requiredCapability: 'ar.write', fallback: '/receivables/credits' } },
    { path: 'credits/:type/:id', component: CreditDetail },
    { path: 'refunds', component: RefundList },
    { path: 'refunds/new', component: RefundEditor, canActivate: [canWrite], data: { requiredCapability: 'ar.write', fallback: '/receivables/refunds' } },
    { path: 'refunds/:id', component: RefundDetail },
  ] },
  { path: 'payables', component: PayablesShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'bills' },
    { path: 'bills', component: BillList },
    { path: 'bills/new', component: BillEditor, canActivate: [canWrite], data: { requiredCapability: 'ap.write', fallback: '/payables/bills' } },
    { path: 'bills/:id/edit', component: BillEditor, canActivate: [canWrite], data: { requiredCapability: 'ap.write', fallback: '/payables/bills' } },
    { path: 'bills/:id', component: BillDetail },
    { path: 'payments', component: BillPaymentList },
    { path: 'payments/new', component: BillPaymentEditor, canActivate: [canWrite], data: { requiredCapability: 'ap.write', fallback: '/payables/payments' } },
    { path: 'payments/:id', component: BillPaymentDetail },
    { path: 'vendors', component: VendorList },
    { path: 'vendors/:id', component: VendorAccount },
    { path: 'credits', component: VendorCreditList },
    { path: 'credits/new', component: VendorCreditApplyEditor, canActivate: [canWrite], data: { requiredCapability: 'ap.write', fallback: '/payables/credits' } },
    { path: 'credits/:id', component: VendorCreditDetail },
  ] },
  { path: 'payroll', component: PayrollShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'runs' },
    { path: 'runs', component: RunList },
    { path: 'runs/new', component: RunEditor, canActivate: [canWrite], data: { requiredCapability: 'payroll.write', fallback: '/payroll/runs' } },
    { path: 'runs/:id', component: RunDetail },
    { path: 'remittances', component: RemittanceList },
    { path: 'remittances/new', component: RemittanceEditor, canActivate: [canWrite], data: { requiredCapability: 'payroll.write', fallback: '/payroll/remittances' } },
    { path: 'remittances/:id', component: RemittanceDetail },
  ] },
  { path: 'fixed-assets', component: FixedAssetsShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'assets' },
    { path: 'assets', component: AssetList },
    { path: 'assets/new', component: AssetEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
    { path: 'assets/:id/edit', component: AssetEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
    { path: 'assets/:id/dispose', component: DisposeEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/assets' } },
    { path: 'assets/:id', component: AssetDetail },
    { path: 'depreciation-runs', component: FaRunList },
    { path: 'depreciation-runs/new', component: FaRunEditor, canActivate: [canWrite], data: { requiredCapability: 'fixedassets.write', fallback: '/fixed-assets/depreciation-runs' } },
    { path: 'depreciation-runs/:id', component: FaRunDetail },
    { path: 'disposals', component: DisposalList },
    { path: 'disposals/:id', component: DisposalDetail },
  ] },
  { path: 'cash', component: BankingShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'cash' },
    { path: 'cash', component: CashList },
    { path: 'cash/disbursements/new', component: CashVoucherEditor, canActivate: [canWrite],
      data: { requiredCapability: 'cash.write', fallback: '/cash/cash', kind: 'disbursement' } },
    { path: 'cash/deposits/new', component: CashVoucherEditor, canActivate: [canWrite],
      data: { requiredCapability: 'cash.write', fallback: '/cash/cash', kind: 'deposit' } },
    { path: 'cash/:id', component: CashVoucherDetail },
    { path: 'statements', component: StatementList },
    { path: 'statements/new', component: StatementEditor, canActivate: [canWrite],
      data: { requiredCapability: 'bankrec.write', fallback: '/cash/statements' } },
    { path: 'statements/import', component: StatementImport, canActivate: [canWrite],
      data: { requiredCapability: 'bankrec.write', fallback: '/cash/statements' } },
    { path: 'statements/:id', component: StatementDetail },
    { path: 'reconciliation', component: ReconciliationList },
    { path: 'reconciliation/:id', component: ReconciliationWorksheet },
  ] },
  { path: 'inventory', children: [
    { path: '', pathMatch: 'full', component: ItemList },
    { path: 'items/new', component: ItemEditor, canActivate: [canWrite], data: { requiredCapability: 'inventory.write', fallback: '/inventory' } },
    { path: 'items/:id/edit', component: ItemEditor, canActivate: [canWrite], data: { requiredCapability: 'inventory.write', fallback: '/inventory' } },
    { path: 'items/:id', component: ItemDetail },
    { path: 'movements/new', component: MovementEditor, canActivate: [canWrite], data: { requiredCapability: 'inventory.write', fallback: '/inventory' } },
    { path: 'movements/:id', component: MovementDetail },
  ] },
  { path: 'admin/users', component: MemberList },
  { path: 'admin/users/:userId', component: MemberEditor, canActivate: [canWrite], data: { requiredCapability: 'admin.users', fallback: '/admin/users' } },
  { path: 'admin/access/sets', component: CapabilitySetList, canActivate: [deploymentAdminGuard('/admin/users')] },
  { path: 'admin/access/sets/new', component: CapabilitySetEditor, canActivate: [deploymentAdminGuard('/admin/users')] },
  { path: 'admin/access/sets/:id', component: CapabilitySetEditor, canActivate: [deploymentAdminGuard('/admin/users')] },
  { path: 'admin/approval-policy', component: ApprovalPolicyScreen, canActivate: [canWrite], data: { requiredCapability: 'admin.approvalPolicy', fallback: '/admin/users' } },
  { path: 'audit/trail', component: AuditTrail },
  // Every nav leaf not served by a built route tree above → Placeholder.
  ...(() => {
    const built = ['/dashboard', '/journal', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables', '/payroll', '/fixed-assets', '/cash', '/inventory', '/admin/users', '/admin/access/sets', '/admin/access/sets/new', '/admin/approval-policy', '/audit/trail'];
    const isBuilt = (p: string) => built.some((b) => p === b || p.startsWith(b + '/'));
    return navLeafPaths()
      .filter((p) => !isBuilt(p))
      .map((p) => ({ path: p.slice(1), component: Placeholder }));
  })(),
  { path: '**', redirectTo: 'dashboard' },
];
