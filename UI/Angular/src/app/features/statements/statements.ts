import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-statements',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0">
      <!-- Tab nav -->
      <nav class="flex gap-1 border-b border-border px-4 pt-4">
        <a
          routerLink="balance-sheet"
          routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
          [routerLinkActiveOptions]="{ exact: false }"
          class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
          data-testid="tab-balance-sheet">
          Balance Sheet
        </a>
        <a
          routerLink="income-statement"
          routerLinkActive="border-b-2 border-primary font-semibold text-foreground"
          [routerLinkActiveOptions]="{ exact: false }"
          class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors"
          data-testid="tab-income-statement">
          Income Statement
        </a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class Statements {}
