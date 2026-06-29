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
  // remaining nav targets → placeholder
  ...NAV.filter(n => ![ '/dashboard', '/trial-balance', '/statements' ].includes(n.path) && !n.path.startsWith('/journal')).map(n => ({ path: n.path.slice(1), component: Placeholder })),
  { path: '**', redirectTo: 'dashboard' },
];
