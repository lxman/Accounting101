import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { SubledgerService } from '../../core/subledger/subledger.service';
import { SubledgerReconciliationLine, SubledgerResponse } from '../../core/subledger/subledger';
import { money as fmtMoney } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-subledger-reconciliations',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Subledger Reconciliations</h1>
      <p class="text-sm text-muted-foreground">
        Each dimensioned control account tied out to its general-ledger balance. A variance is the
        remainder posted to the account without the dimension tag.
      </p>

      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (!loading() && !error()) {
        @if (rows().length === 0) {
          <p class="text-muted-foreground text-sm">No dimensioned control accounts to reconcile.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr><th hlmTh>Account</th><th hlmTh>Dimension</th><th hlmTh class="text-right">Control</th><th hlmTh class="text-right">Subledger</th><th hlmTh class="text-right">Variance</th><th hlmTh>Status</th></tr>
              </thead>
              <tbody hlmTBody>
                @for (r of rows(); track key(r)) {
                  <tr hlmTr role="button" tabindex="0" class="cursor-pointer hover:bg-muted/50"
                      (click)="toggle(r)" (keydown.enter)="toggle(r)">
                    <td hlmTd>{{ expanded().has(key(r)) ? '▾' : '▸' }} {{ r.number }} {{ r.name }}</td>
                    <td hlmTd>{{ r.dimension }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ money(r.controlBalance) }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ money(r.subledgerTotal) }}</td>
                    <td hlmTd class="text-right tabular-nums" [class.text-destructive]="!r.tiesOut">{{ money(r.variance) }}</td>
                    <td hlmTd>
                      @if (r.tiesOut) {
                        <span class="text-sm px-2 py-0.5 rounded border border-border text-green-700 dark:text-green-400">Ties out</span>
                      } @else {
                        <span class="text-sm px-2 py-0.5 rounded border border-destructive text-destructive">Variance</span>
                      }
                    </td>
                  </tr>
                  @if (expanded().has(key(r))) {
                    <tr hlmTr>
                      <td hlmTd colspan="6" class="bg-muted/30">
                        @if (breakdowns()[key(r)]; as b) {
                          @if (b === 'error') {
                            <p class="text-destructive text-sm">Could not load the breakdown.</p>
                          } @else if (b === 'loading') {
                            <p class="text-muted-foreground text-sm">Loading…</p>
                          } @else {
                            <table class="text-sm w-full max-w-lg">
                              <tbody>
                                @for (l of b.lines; track l.dimensionValue) {
                                  <tr>
                                    <td class="py-0.5">{{ l.number ?? l.name ?? l.dimensionValue }}</td>
                                    <td class="py-0.5 text-right tabular-nums">{{ money(l.balance) }}</td>
                                  </tr>
                                }
                                @if (r.variance !== 0) {
                                  <tr class="border-t border-border text-destructive">
                                    <td class="py-0.5">Untagged remainder (no {{ r.dimension }})</td>
                                    <td class="py-0.5 text-right tabular-nums">{{ money(r.variance) }}</td>
                                  </tr>
                                }
                              </tbody>
                            </table>
                          }
                        }
                      </td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          </div>
        }
      }
    </div>
  `,
})
export class SubledgerReconciliations {
  private readonly svc = inject(SubledgerService);
  private readonly destroyRef = inject(DestroyRef);

  readonly rows = signal<SubledgerReconciliationLine[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor() {
    this.svc.reconciliations().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.rows.set(r.lines); this.loading.set(false); },
      error: (e) => { this.error.set(extractProblem(e).detail); this.loading.set(false); },
    });
  }

  key(r: SubledgerReconciliationLine): string { return `${r.account}|${r.dimension}`; }
  money(n: number): string { return fmtMoney(n); }

  readonly expanded = signal<Set<string>>(new Set());
  readonly breakdowns = signal<Record<string, SubledgerResponse | 'loading' | 'error'>>({});

  toggle(r: SubledgerReconciliationLine): void {
    const k = this.key(r);
    const next = new Set(this.expanded());
    if (next.has(k)) { next.delete(k); this.expanded.set(next); return; }
    next.add(k);
    this.expanded.set(next);
    if (this.breakdowns()[k]) return;   // already loaded/loading — do not re-fetch
    this.breakdowns.update((m) => ({ ...m, [k]: 'loading' }));
    this.svc.breakdown(r.account, r.dimension).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (b) => this.breakdowns.update((m) => ({ ...m, [k]: b })),
      error: () => this.breakdowns.update((m) => ({ ...m, [k]: 'error' })),
    });
  }
}
