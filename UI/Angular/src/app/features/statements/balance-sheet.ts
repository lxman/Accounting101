import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { ClientContextService } from '../../core/client/client-context.service';
import { StatementsService } from '../../core/statements/statements.service';
import { BalanceSheetResponse } from '../../core/statements/statement';
import { formatMoney } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { StatementSectionComponent } from './statement-section';

@Component({
  selector: 'app-balance-sheet',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmBadgeImports, ...HlmCardImports, StatementSectionComponent],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h2 class="text-xl font-bold">Balance Sheet</h2>
        <div class="ms-auto flex items-center gap-2">
          <label for="bs-asof" class="text-sm text-muted-foreground">As of</label>
          <input
            id="bs-asof"
            type="date"
            [value]="asOf()"
            (change)="onAsOfChange($event)"
            class="border border-input rounded px-2 py-1 text-sm bg-background"
            data-testid="bs-asof-input"
          />
        </div>
      </div>

      @if (loading()) {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }

      @if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      }

      @if (!loading() && !error() && data()) {
        <div hlmCard class="p-4">
          <app-statement-section [section]="data()!.assets" />
          <app-statement-section [section]="data()!.liabilities" />
          <app-statement-section [section]="data()!.equity" />

          <!-- Grand totals — double rule -->
          <table class="w-full text-sm mt-2">
            <tfoot>
              <tr class="border-t-4 border-double border-foreground font-bold">
                <td class="pt-1 pr-4">Total Assets</td>
                <td
                  class="text-end tabular-nums pt-1 w-32"
                  data-testid="total-assets">
                  {{ fmtTotal(data()!.totalAssets) }}
                </td>
              </tr>
              <tr class="border-t-4 border-double border-foreground font-bold">
                <td class="pt-1 pr-4">Total Liabilities &amp; Equity</td>
                <td
                  class="text-end tabular-nums pt-1 w-32"
                  data-testid="total-liabilities-equity">
                  {{ fmtTotal(data()!.totalLiabilitiesAndEquity) }}
                </td>
              </tr>
            </tfoot>
          </table>

          <!-- isBalanced badge -->
          <div class="mt-3 flex items-center gap-2">
            <span class="text-sm text-muted-foreground">Status:</span>
            @if (data()!.isBalanced) {
              <span
                hlmBadge
                style="background-color: var(--brand-teal); color: white;"
                data-testid="balanced-badge">
                Balanced
              </span>
            } @else {
              <span
                hlmBadge
                variant="destructive"
                data-testid="unbalanced-badge">
                Out of Balance
              </span>
            }
          </div>
        </div>
      }
    </div>
  `,
})
export class BalanceSheet {
  private readonly svc = inject(StatementsService);
  private readonly client = inject(ClientContextService);

  readonly asOf = signal<string>(new Date().toISOString().slice(0, 10));
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({
    id: this.client.clientId(),
    asOf: this.asOf() || undefined,
  }));

  readonly data = toSignal(
    toObservable(this.query).pipe(
      tap(() => {
        this.loading.set(true);
        this.error.set(null);
      }),
      switchMap(({ id, asOf }) => {
        if (!id) {
          this.loading.set(false);
          return of(null);
        }
        return this.svc.balanceSheet(asOf).pipe(
          tap(() => this.loading.set(false)),
          catchError((e: unknown) => {
            this.error.set(
              (e as { message?: string })?.message ?? 'Error loading balance sheet',
            );
            this.loading.set(false);
            return of(null);
          }),
        );
      }),
    ),
    { initialValue: null as BalanceSheetResponse | null },
  );

  fmtTotal(amount: number): string {
    return formatMoney(amount, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true });
  }

  onAsOfChange(e: Event): void {
    this.asOf.set((e.target as HTMLInputElement).value);
  }
}
