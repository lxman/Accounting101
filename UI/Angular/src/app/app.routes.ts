import { Routes } from '@angular/router';
import { Dashboard } from './features/dashboard/dashboard';
import { Placeholder } from './features/placeholder/placeholder';
import { NAV } from './layout/nav';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', component: Dashboard },
  // every other nav target → placeholder for 1a
  ...NAV.filter(n => n.path !== '/dashboard').map(n => ({ path: n.path.slice(1), component: Placeholder })),
  { path: '**', redirectTo: 'dashboard' },
];
