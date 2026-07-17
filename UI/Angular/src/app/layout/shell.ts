import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink, RouterOutlet } from '@angular/router';
import { ClientContextService } from '../core/client/client-context.service';
import { ThemeSwitch } from '../core/theme/theme-switch';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { DevIdentityService } from '../core/api/dev-identity.service';
import { CapabilityService } from '../core/capabilities/capability.service';
import { NAV, visibleSections } from './nav';
import { NavStateService } from './nav-state.service';

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, ThemeSwitch, ...HlmSelectImports],
  template: `
    <div class="flex flex-col h-screen overflow-hidden bg-background text-foreground">
      <header class="shrink-0 flex items-center gap-3 px-4 h-14 bg-card border-b border-border">
        <button type="button" data-testid="sidebar-toggle"
                (click)="navState.toggleSidebar()"
                [attr.aria-label]="navState.sidebarCollapsed() ? 'Show sidebar' : 'Hide sidebar'"
                class="text-lg px-2 py-1 rounded-lg hover:bg-accent">☰</button>
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
      <div class="flex flex-1 min-h-0">
        @if (!navState.sidebarCollapsed()) {
        <aside class="w-56 shrink-0 overflow-y-auto p-2 bg-sidebar text-sidebar-foreground">
          @for (section of visibleNav(); track section.label) {
            <div class="mt-3 first:mt-0">
              <button type="button" data-testid="nav-section-header"
                      (click)="navState.toggleSection(section.label)"
                      class="w-full flex items-center justify-between px-3 py-1 text-xs uppercase tracking-wide text-muted-foreground">
                <span>{{ section.label }}</span>
                <span>{{ navState.isSectionOpen(section.label) ? '▾' : '▸' }}</span>
              </button>
              @if (navState.isSectionOpen(section.label)) {
                @for (item of section.items; track item.path) {
                  <div class="flex items-center">
                    <a [routerLink]="item.path"
                       class="flex-1 block px-3 py-2 rounded-lg text-sm"
                       [class.bg-sidebar-accent]="navState.activePath() === item.path"
                       [class.text-sidebar-accent-foreground]="navState.activePath() === item.path"
                       [class.font-semibold]="navState.activePath() === item.path">{{ item.label }}</a>
                    @if (item.children) {
                      <button type="button"
                              data-testid="nav-parent-toggle"
                              (click)="navState.toggleParent(item.path)"
                              class="px-2 py-1 text-xs text-muted-foreground">
                        {{ navState.isParentOpen(item.path) ? '▾' : '▸' }}
                      </button>
                    }
                  </div>
                  @if (item.children && navState.isParentOpen(item.path)) {
                    @for (child of item.children; track child.path) {
                      <a [routerLink]="child.path"
                         class="block pl-6 pr-3 py-1.5 rounded-lg text-sm"
                         [class.bg-sidebar-accent]="navState.activePath() === child.path"
                         [class.text-sidebar-accent-foreground]="navState.activePath() === child.path"
                         [class.font-semibold]="navState.activePath() === child.path">{{ child.label }}</a>
                    }
                  }
                }
              }
            </div>
          }
        </aside>
        }
        <main class="flex-1 min-w-0 overflow-y-auto p-6"><router-outlet /></main>
      </div>
    </div>`,
})
export class Shell {
  protected readonly client = inject(ClientContextService);
  protected readonly identity = inject(DevIdentityService);
  protected readonly navState = inject(NavStateService);
  protected readonly caps = inject(CapabilityService);
  protected readonly visibleNav = computed(() =>
    visibleSections(NAV, (link) =>
      (!link.area || this.caps.hasArea(link.area)) &&
      (!link.moduleKey || this.caps.moduleEnabled(link.moduleKey)) &&
      (!link.deploymentAdmin || this.caps.deploymentAdmin())));

  // The trigger renders the active value (a user sub) via itemToString; map it back to a readable
  // "Acting as: <name>" (a bare value would display the raw GUID).
  protected readonly identityItemToString = (sub: string): string =>
    `Acting as: ${this.identity.identities.find((i) => i.sub === sub)?.name ?? sub}`;
}
