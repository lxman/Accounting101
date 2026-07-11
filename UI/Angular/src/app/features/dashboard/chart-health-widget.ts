import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ChartHealthService } from '../../core/chart-health/chart-health.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountReadinessResult, CHART_HEALTH_MODULES, ModuleHealth } from '../../core/chart-health/chart-health';

@Component({
  selector: 'app-chart-health-widget',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="rounded-lg border p-4 flex flex-col gap-3 max-w-xl">
      <header class="flex items-center justify-between">
        <h2 class="font-semibold">Chart Health</h2>
        <span class="text-sm text-muted-foreground">{{ readyCount() }} / {{ total }} ready</span>
      </header>

      @if (loading()) {
        <p class="text-sm text-muted-foreground">Checking…</p>
      } @else {
        <ul class="flex flex-col divide-y">
          @for (m of modules(); track m.key) {
            <li class="py-2">
              <div class="flex items-center justify-between">
                <span>{{ m.label }}</span>
                @if (m.errored) {
                  <span class="text-sm text-muted-foreground">couldn't check</span>
                } @else if (m.report?.ready) {
                  <span class="text-sm text-green-600" aria-label="ready">✓ ready</span>
                } @else {
                  <button type="button" class="text-sm text-destructive underline" (click)="toggle(m.key)">
                    {{ gapCount(m) }} gap{{ gapCount(m) === 1 ? '' : 's' }} {{ expanded().has(m.key) ? '▾' : '›' }}
                  </button>
                }
              </div>
              @if (expanded().has(m.key) && m.report) {
                <ul class="mt-2 flex flex-col gap-1 pl-3 text-sm">
                  @for (g of gaps(m); track g.accountId) {
                    <li class="flex flex-col">
                      <span class="text-muted-foreground">{{ g.label }} — {{ g.status }}</span>
                      <span>{{ g.detail }}</span>
                      <a class="text-primary underline w-fit" [routerLink]="gapLink(g)" [queryParams]="gapQuery(g)">Fix ›</a>
                    </li>
                  }
                </ul>
              }
            </li>
          }
        </ul>
      }
    </section>
  `,
})
export class ChartHealthWidget {
  private readonly health = inject(ChartHealthService);
  private readonly client = inject(ClientContextService);

  readonly total = CHART_HEALTH_MODULES.length;
  readonly modules = signal<ModuleHealth[]>([]);
  readonly loading = signal(false);
  readonly expanded = signal<Set<string>>(new Set());

  readonly readyCount = computed(() => this.modules().filter(m => m.report?.ready).length);

  constructor() {
    effect((onCleanup) => {
      const id = this.client.clientId();
      if (!id) { this.modules.set([]); this.loading.set(false); return; }
      this.loading.set(true);
      const sub = this.health.readiness().subscribe(m => { this.modules.set(m); this.loading.set(false); });
      onCleanup(() => sub.unsubscribe());
    });
  }

  gapCount(m: ModuleHealth): number { return this.gaps(m).length; }
  gaps(m: ModuleHealth): AccountReadinessResult[] { return (m.report?.accounts ?? []).filter(a => a.status !== 'Ok'); }

  toggle(key: string): void {
    this.expanded.update(set => {
      const next = new Set(set);
      next.has(key) ? next.delete(key) : next.add(key);
      return next;
    });
  }

  gapLink(g: AccountReadinessResult): (string)[] {
    return g.status === 'Missing' ? ['/accounts', 'new'] : ['/accounts', g.accountId, 'edit'];
  }

  gapQuery(g: AccountReadinessResult): Record<string, string> | undefined {
    if (g.status !== 'Missing') return undefined;
    return { id: g.accountId, type: g.expectedType ?? '', name: g.label, dims: g.requiredDimensions.join(',') };
  }
}
