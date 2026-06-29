import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { ClientContextService } from '../core/client/client-context.service';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { NAV } from './nav';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, ThemeSwitch, HlmButton, ...HlmSelectImports],
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
          <div hlmSelect [value]="identity.active().sub" [itemToString]="identityItemToString" (valueChange)="identity.use($any($event))" class="w-44">
            <hlm-select-trigger class="w-44">
              <hlm-select-value placeholder="Acting as…" />
            </hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal>
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
            <a [routerLink]="item.path"
               class="block px-3 py-2 rounded-lg text-sm"
               [class.bg-sidebar-accent]="activePath() === item.path"
               [class.text-sidebar-accent-foreground]="activePath() === item.path"
               [class.font-semibold]="activePath() === item.path">{{ item.label }}</a>
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
  private readonly router = inject(Router);

  // Current URL as a signal (seeded with the initial URL, updated on each navigation).
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  // The highlighted nav item is the one whose path is the LONGEST prefix of the current URL, so a
  // child route lights its section (e.g. /journal/:id → Journal, /statements/balance-sheet →
  // Statements) while a more specific sibling still wins (/journal/approvals → Approvals).
  protected readonly activePath = computed(() => {
    const u = this.url();
    return (
      this.nav
        .filter((n) => u === n.path || u.startsWith(n.path + '/'))
        .sort((a, b) => b.path.length - a.path.length)[0]?.path ?? null
    );
  });

  // The trigger renders the active value (a user sub) via itemToString; map it back to a readable
  // "Acting as: <name>" (a bare value would display the raw GUID).
  protected readonly identityItemToString = (sub: string): string =>
    `Acting as: ${this.identity.identities.find((i) => i.sub === sub)?.name ?? sub}`;
}
