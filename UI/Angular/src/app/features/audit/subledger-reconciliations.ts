import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { SubledgerService } from '../../core/subledger/subledger.service';
import { SubledgerReconciliationLine } from '../../core/subledger/subledger';
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
                  <tr hlmTr>
                    <td hlmTd>{{ r.number }} {{ r.name }}</td>
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
}
