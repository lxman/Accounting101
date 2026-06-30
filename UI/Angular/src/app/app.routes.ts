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
import { NAV } from './layout/nav';

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
    { path: 'invoices/new', component: InvoiceEditor },
    { path: 'invoices/:id/edit', component: InvoiceEditor },
    { path: 'invoices/:id', component: InvoiceDetail },
    { path: 'payments', component: PaymentList },
    { path: 'payments/new', component: PaymentEditor },
    { path: 'customers', component: CustomerList },
    { path: 'credits', component: CreditList },
  ] },
  // remaining nav targets → placeholder
  ...NAV.filter(n => ![ '/dashboard', '/trial-balance', '/statements', '/accounts', '/receivables' ].includes(n.path) && !n.path.startsWith('/journal')).map(n => ({ path: n.path.slice(1), component: Placeholder })),
  { path: '**', redirectTo: 'dashboard' },
];
