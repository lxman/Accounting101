import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-banking-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0">
      <nav class="flex gap-1 border-b border-border px-4 pt-4">
        <a routerLink="cash" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-cash">Cash</a>
        <a routerLink="statements" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-statements">Statements</a>
        <a routerLink="reconciliation" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-reconcile">Reconcile</a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class BankingShell {}
