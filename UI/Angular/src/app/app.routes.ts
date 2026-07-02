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
import { CustomerAccount } from './features/receivables/customer-account';
import { PayablesShell } from './features/payables/payables-shell';
import { VendorList } from './features/payables/vendor-list';
import { BillList } from './features/payables/bill-list';
import { BillEditor } from './features/payables/bill-editor';
import { BillDetail } from './features/payables/bill-detail';
import { BillPaymentList } from './features/payables/bill-payment-list';
import { BillPaymentEditor } from './features/payables/bill-payment-editor';
import { VendorCreditList } from './features/payables/vendor-credit-list';
import { VendorCreditApplyEditor } from './features/payables/vendor-credit-apply-editor';
import { VendorAccount } from './features/payables/vendor-account';
import { PayrollShell } from './features/payroll/payroll-shell';
import { RunList } from './features/payroll/run-list';
import { RunEditor } from './features/payroll/run-editor';
import { RunDetail } from './features/payroll/run-detail';
import { RemittanceList } from './features/payroll/remittance-list';
import { RemittanceEditor } from './features/payroll/remittance-editor';
import { RemittanceDetail } from './features/payroll/remittance-detail';
import { navLeafPaths } from './layout/nav';
import { canWrite } from './core/capabilities/can.guard';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', component: Dashboard },
  {
    path: 'journal',
    children: [
      { path: '', pathMatch: 'full', component: EntryList },
      { path: 'new', component: EntryForm },
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
    { path: 'new', component: AccountEditor },
    { path: ':id/edit', component: AccountEditor },
  ] },
  { path: 'receivables', component: ReceivablesShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'invoices' },
    { path: 'invoices', component: InvoiceList },
    { path: 'invoices/new', component: InvoiceEditor, canActivate: [canWrite('ar.write', '/receivables/invoices')] },
    { path: 'invoices/:id/edit', component: InvoiceEditor, canActivate: [canWrite('ar.write', '/receivables/invoices')] },
    { path: 'invoices/:id', component: InvoiceDetail },
    { path: 'payments', component: PaymentList },
    { path: 'payments/new', component: PaymentEditor, canActivate: [canWrite('ar.write', '/receivables/payments')] },
    { path: 'customers', component: CustomerList },
    { path: 'customers/:id', component: CustomerAccount },
    { path: 'credits', component: CreditList },
    { path: 'credits/new', component: AdjustmentEditor, canActivate: [canWrite('ar.write', '/receivables/credits')] },
    { path: 'refunds', component: RefundList },
    { path: 'refunds/new', component: RefundEditor, canActivate: [canWrite('ar.write', '/receivables/refunds')] },
  ] },
  { path: 'payables', component: PayablesShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'bills' },
    { path: 'bills', component: BillList },
    { path: 'bills/new', component: BillEditor, canActivate: [canWrite('ap.write', '/payables/bills')] },
    { path: 'bills/:id/edit', component: BillEditor, canActivate: [canWrite('ap.write', '/payables/bills')] },
    { path: 'bills/:id', component: BillDetail },
    { path: 'payments', component: BillPaymentList },
    { path: 'payments/new', component: BillPaymentEditor, canActivate: [canWrite('ap.write', '/payables/payments')] },
    { path: 'vendors', component: VendorList },
    { path: 'vendors/:id', component: VendorAccount },
    { path: 'credits', component: VendorCreditList },
    { path: 'credits/new', component: VendorCreditApplyEditor, canActivate: [canWrite('ap.write', '/payables/credits')] },
  ] },
  { path: 'payroll', component: PayrollShell, children: [
    { path: '', pathMatch: 'full', redirectTo: 'runs' },
    { path: 'runs', component: RunList },
    { path: 'runs/new', component: RunEditor, canActivate: [canWrite('payroll.write', '/payroll/runs')] },
    { path: 'runs/:id', component: RunDetail },
    { path: 'remittances', component: RemittanceList },
    { path: 'remittances/new', component: RemittanceEditor, canActivate: [canWrite('payroll.write', '/payroll/remittances')] },
    { path: 'remittances/:id', component: RemittanceDetail },
  ] },
  // Every nav leaf not served by a built route tree above → Placeholder.
  ...(() => {
    const built = ['/dashboard', '/journal', '/trial-balance', '/statements', '/accounts', '/receivables', '/payables', '/payroll'];
    const isBuilt = (p: string) => built.some((b) => p === b || p.startsWith(b + '/'));
    return navLeafPaths()
      .filter((p) => !isBuilt(p))
      .map((p) => ({ path: p.slice(1), component: Placeholder }));
  })(),
  { path: '**', redirectTo: 'dashboard' },
];
