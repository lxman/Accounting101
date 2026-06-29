import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { ClientContextService } from '../../core/client/client-context.service';
import { StatementsService } from '../../core/statements/statements.service';
import { IncomeStatementResponse } from '../../core/statements/statement';
import { formatMoney, isNegativeAmount } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { StatementSectionComponent } from './statement-section';

function firstOfMonth(): string {
  const d = new Date();
  return new Date(d.getFullYear(), d.getMonth(), 1).toISOString().slice(0, 10);
}

function today(): string {
  return new Date().toISOString().slice(0, 10);
}

@Component({
  selector: 'app-income-statement',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmCardImports, StatementSectionComponent],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h2 class="text-xl font-bold">Income Statement</h2>
        <div class="ms-auto flex items-center gap-2 flex-wrap">
          <label for="is-from" class="text-sm text-muted-foreground">From</label>
          <input
            id="is-from"
            type="date"
            [value]="from()"
            (change)="onFromChange($event)"
            class="border border-input rounded px-2 py-1 text-sm bg-background"
            data-testid="is-from-input"
          />
          <label for="is-to" class="text-sm text-muted-foreground">To</label>
          <input
            id="is-to"
            type="date"
            [value]="to()"
            (change)="onToChange($event)"
            class="border border-input rounded px-2 py-1 text-sm bg-background"
            data-testid="is-to-input"
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
          <app-statement-section [section]="data()!.revenue" />
          <app-statement-section [section]="data()!.expenses" />

          <!-- Net Income — double rule -->
          <table class="w-full text-sm mt-2">
            <tfoot>
              <tr class="border-t-4 border-double border-foreground font-bold">
                <td class="pt-1 pr-4">Net Income</td>
                <td
                  class="text-end tabular-nums pt-1 w-32"
                  [class.text-destructive]="isNeg(data()!.netIncome)"
                  data-testid="net-income">
                  {{ fmtTotal(data()!.netIncome) }}
                </td>
              </tr>
            </tfoot>
          </table>
        </div>
      }
    </div>
  `,
})
export class IncomeStatement {
  private readonly svc = inject(StatementsService);
  private readonly client = inject(ClientContextService);

  readonly from = signal<string>(firstOfMonth());
  readonly to = signal<string>(today());
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  // Both from AND to are always sent (required by the backend).
  private readonly query = computed(() => ({
    id: this.client.clientId(),
    from: this.from(),
    to: this.to(),
  }));

  readonly data = toSignal(
    toObservable(this.query).pipe(
      tap(() => {
        this.loading.set(true);
        this.error.set(null);
      }),
      switchMap(({ id, from, to }) => {
        if (!id || !from || !to) {
          this.loading.set(false);
          return of(null);
        }
        return this.svc.incomeStatement(from, to).pipe(
          tap(() => this.loading.set(false)),
          catchError((e: unknown) => {
            this.error.set(
              (e as { message?: string })?.message ?? 'Error loading income statement',
            );
            this.loading.set(false);
            return of(null);
          }),
        );
      }),
    ),
    { initialValue: null as IncomeStatementResponse | null },
  );

  fmtTotal(amount: number): string {
    return formatMoney(amount, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true });
  }

  isNeg(amount: number): boolean {
    return isNegativeAmount(amount);
  }

  onFromChange(e: Event): void {
    this.from.set((e.target as HTMLInputElement).value);
  }

  onToChange(e: Event): void {
    this.to.set((e.target as HTMLInputElement).value);
  }
}
