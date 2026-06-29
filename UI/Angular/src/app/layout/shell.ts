import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ClientContextService } from '../core/client/client-context.service';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { NAV } from './nav';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ThemeSwitch, HlmButton, ...HlmSelectImports],
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
          <div hlmSelect [value]="identity.active().sub" (valueChange)="identity.use($any($event))" class="w-44">
            <hlm-select-trigger class="w-44">
              <hlm-select-value placeholder="Acting as…">Acting as: {{ identity.active().name }}</hlm-select-value>
            </hlm-select-trigger>
            <hlm-select-content>
              @for (id of identity.identities; track id.sub) {
                <hlm-select-item [value]="id.sub">{{ id.name }}</hlm-select-item>
              }
            </hlm-select-content>
          </div>
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
  protected readonly identity = inject(DevIdentityService);
}
