import { Routes } from '@angular/router';
import { Dashboard } from './features/dashboard/dashboard';
import { EntryList } from './features/journal/entry-list';
import { Placeholder } from './features/placeholder/placeholder';
import { NAV } from './layout/nav';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', component: Dashboard },
  { path: 'journal', component: EntryList },
  // remaining nav targets → placeholder
  ...NAV.filter(n => n.path !== '/dashboard' && n.path !== '/journal').map(n => ({ path: n.path.slice(1), component: Placeholder })),
  { path: '**', redirectTo: 'dashboard' },
];
