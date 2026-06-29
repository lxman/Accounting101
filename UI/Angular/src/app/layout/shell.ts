import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ClientContextService } from '../core/client/client-context.service';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { NAV } from './nav';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeSwitch],
  template: `
    <div class="min-h-screen bg-[color:var(--color-bg)] text-[color:var(--color-ink)]">
      <header class="flex items-center gap-3 px-4 h-14 bg-[color:var(--color-surface)] border-b border-[color:var(--color-border)]">
        <button type="button" class="text-sm font-semibold px-3 py-1.5 rounded-lg border border-[color:var(--color-border)]">
          {{ client.clientId() ?? 'Select client' }} ▾
        </button>
        <div class="ml-auto flex items-center gap-2">
          <button type="button" class="text-sm px-2.5 py-1.5 rounded-lg hover:bg-[color:var(--color-bg)]">Edit Firm</button>
          <button type="button" class="text-sm px-2.5 py-1.5 rounded-lg hover:bg-[color:var(--color-bg)]">Edit Client</button>
          <app-theme-switch />
          <span class="text-sm text-[color:var(--color-muted)]">Jordan ▾</span>
        </div>
      </header>
      <div class="flex">
        <aside class="w-44 min-h-[calc(100vh-3.5rem)] p-2 bg-[color:var(--color-sidebar)] text-[color:var(--color-sidebar-ink)]">
          @for (item of nav; track item.path) {
            <a [routerLink]="item.path" routerLinkActive="bg-white/10 text-white font-semibold"
               class="block px-3 py-2 rounded-lg text-sm">{{ item.label }}</a>
          }
        </aside>
        <main class="flex-1 p-6"><router-outlet /></main>
      </div>
    </div>`,
})
export class Shell {
  protected readonly nav = NAV;
  protected readonly client = inject(ClientContextService);
}
