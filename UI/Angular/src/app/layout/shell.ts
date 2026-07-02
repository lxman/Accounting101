import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { ClientContextService } from '../core/client/client-context.service';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { NAV, NavLink, navLeafPaths } from './nav';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, ThemeSwitch, ...HlmSelectImports],
  template: `
    <div class="min-h-screen bg-background text-foreground">
      <header class="flex items-center gap-3 px-4 h-14 bg-card border-b border-border">
        <button type="button" class="text-sm font-semibold px-3 py-1.5 rounded-lg border border-border">
          {{ client.clientId() ?? 'Select client' }} ▾
        </button>
        <div class="ml-auto flex items-center gap-2">
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
        <aside class="w-56 min-h-[calc(100vh-3.5rem)] p-2 bg-sidebar text-sidebar-foreground">
          @for (section of nav; track section.label) {
            <div class="mt-3 first:mt-0">
              <button type="button" data-testid="nav-section-header"
                      (click)="toggle(section.label)"
                      class="w-full flex items-center justify-between px-3 py-1 text-xs uppercase tracking-wide text-muted-foreground">
                <span>{{ section.label }}</span>
                @if (!sectionContainsActive(section.label)) {
                  <span>{{ isOpen(section.label) ? '▾' : '▸' }}</span>
                }
              </button>
              @if (isOpen(section.label)) {
                @for (item of section.items; track item.path) {
                  <div class="flex items-center">
                    <a [routerLink]="item.path"
                       class="flex-1 block px-3 py-2 rounded-lg text-sm"
                       [class.bg-sidebar-accent]="activePath() === item.path"
                       [class.text-sidebar-accent-foreground]="activePath() === item.path"
                       [class.font-semibold]="activePath() === item.path">{{ item.label }}</a>
                    @if (item.children && !parentContainsActive(item)) {
                      <button type="button"
                              (click)="toggle(item.path)"
                              class="px-2 py-1 text-xs text-muted-foreground">
                        {{ parentOpen(item) ? '▾' : '▸' }}
                      </button>
                    }
                  </div>
                  @if (item.children && parentOpen(item)) {
                    @for (child of item.children; track child.path) {
                      <a [routerLink]="child.path"
                         class="block pl-6 pr-3 py-1.5 rounded-lg text-sm"
                         [class.bg-sidebar-accent]="activePath() === child.path"
                         [class.text-sidebar-accent-foreground]="activePath() === child.path"
                         [class.font-semibold]="activePath() === child.path">{{ child.label }}</a>
                    }
                  }
                }
              }
            </div>
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

  private readonly leafPaths = navLeafPaths();
  private readonly collapsed = signal<Set<string>>(new Set());

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  // Highlighted item = longest nav leaf path that prefixes the current URL.
  protected readonly activePath = computed(() => {
    const u = this.url();
    return (
      this.leafPaths
        .filter((p) => u === p || u.startsWith(p + '/'))
        .sort((a, b) => b.length - a.length)[0] ?? null
    );
  });

  toggle(key: string): void {
    this.collapsed.update((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  }

  // A group is open when not explicitly collapsed OR when it contains the active path
  // (so navigating never hides the current page).
  isOpen(sectionLabel: string): boolean {
    if (!this.collapsed().has(sectionLabel)) return true;
    return this.sectionContainsActive(sectionLabel);
  }

  parentOpen(item: NavLink): boolean {
    if (!this.collapsed().has(item.path)) return true;
    const a = this.activePath();
    return a === item.path || (item.children?.some((c) => c.path === a) ?? false);
  }

  parentContainsActive(item: NavLink): boolean {
    const a = this.activePath();
    if (!a) return false;
    return a === item.path || (item.children?.some((c) => c.path === a) ?? false);
  }

  sectionContainsActive(sectionLabel: string): boolean {
    const a = this.activePath();
    if (!a) return false;
    const section = NAV.find((s) => s.label === sectionLabel);
    if (!section) return false;
    return section.items.some((i) => i.path === a || (i.children?.some((c) => c.path === a) ?? false));
  }

  protected readonly identityItemToString = (sub: string): string =>
    `Acting as: ${this.identity.identities.find((i) => i.sub === sub)?.name ?? sub}`;
}
