import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ClientContextService } from '../core/client/client-context.service';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { HlmButton } from '../shared/ui/ui-button-helm/hlm-button';
import { NAV } from './nav';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeSwitch, HlmButton],
  template: `
    <div class="min-h-screen bg-background text-foreground">
      <header class="flex items-center gap-3 px-4 h-14 bg-card border-b border-border">
        <button type="button" class="text-sm font-semibold px-3 py-1.5 rounded-lg border border-border">
          {{ client.clientId() ?? 'Select client' }} ▾
        </button>
        <div class="ml-auto flex items-center gap-2">
          <button hlmBtn type="button" variant="ghost" size="sm">Edit Firm</button>
          <button hlmBtn type="button" variant="ghost" size="sm">Edit Client</button>
          <app-theme-switch />
          <span class="text-sm text-muted-foreground">Jordan ▾</span>
        </div>
      </header>
      <div class="flex">
        <aside class="w-44 min-h-[calc(100vh-3.5rem)] p-2 bg-sidebar text-sidebar-foreground">
          @for (item of nav; track item.path) {
            <a [routerLink]="item.path" routerLinkActive="bg-sidebar-accent text-sidebar-accent-foreground font-semibold"
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
