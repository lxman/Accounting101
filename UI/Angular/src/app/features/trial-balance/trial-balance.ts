import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmCardImports } from '@spartan-ng/helm/card';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountsService } from '../../core/accounts/accounts.service';
import { TrialBalanceService } from '../../core/trial-balance/trial-balance.service';
import { TrialBalanceResponse } from '../../core/trial-balance/trial-balance';
import { formatMoney } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';

interface DisplayRow {
  accountId: string;
  label: string;
  debit: string | null;
  credit: string | null;
}

@Component({
  selector: 'app-trial-balance',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports, ...HlmCardImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Trial Balance</h1>
        <div class="ms-auto flex items-center gap-2">
          <label for="asof-input" class="text-sm text-muted-foreground">As of</label>
          <input
            id="asof-input"
            type="date"
            [value]="asOf()"
            (change)="onAsOfChange($event)"
            class="border border-input rounded px-2 py-1 text-sm bg-background"
            data-testid="asof-input"
          />
        </div>
      </div>

      @if (loading()) {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }

      @if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      }

      @if (outOfBalance()) {
        <p class="text-destructive text-sm font-semibold" data-testid="out-of-balance">
          ⚠ Out of balance — debits and credits do not foot equal.
        </p>
      }

      @if (!loading() && !error()) {
        @if (rows().length === 0) {
          <p class="text-muted-foreground text-sm">No accounts in the trial balance.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh class="w-1/2">Account</th>
                  <th hlmTh class="text-end tabular-nums w-1/4">Debit</th>
                  <th hlmTh class="text-end tabular-nums w-1/4">Credit</th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (row of rows(); track row.accountId) {
                  <tr hlmTr>
                    <td hlmTd>{{ row.label }}</td>
                    <td hlmTd class="text-end tabular-nums" data-testid="debit-cell">
                      {{ row.debit ?? '' }}
                    </td>
                    <td hlmTd class="text-end tabular-nums" data-testid="credit-cell">
                      {{ row.credit ?? '' }}
                    </td>
                  </tr>
                }
              </tbody>
              <tfoot hlmTFoot>
                <tr hlmTr class="border-t-2 border-foreground font-semibold">
                  <td hlmTd>Totals</td>
                  <td hlmTd
                    class="text-end tabular-nums border-t-4 border-double border-foreground"
                    data-testid="debit-total">
                    {{ debitTotal() }}
                  </td>
                  <td hlmTd
                    class="text-end tabular-nums border-t-4 border-double border-foreground"
                    data-testid="credit-total">
                    {{ creditTotal() }}
                  </td>
                </tr>
              </tfoot>
            </table>
          </div>
        }
      }
    </div>
  `,
})
export class TrialBalance {
  private readonly tbSvc = inject(TrialBalanceService);
  private readonly accountsSvc = inject(AccountsService);
  private readonly client = inject(ClientContextService);

  readonly asOf = signal<string>(new Date().toISOString().slice(0, 10));
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  // The query: re-emits when clientId or asOf changes.
  private readonly query = computed(() => ({
    id: this.client.clientId(),
    asOf: this.asOf() || undefined,
  }));

  // Reactive load: switchMap cancels in-flight requests when the query changes.
  private readonly result = toSignal(
    toObservable(this.query).pipe(
      tap(({ id }) => {
        this.loading.set(true);
        this.error.set(null);
        // Ensure account labels are available for the join.
        if (id && this.accountsSvc.accounts().length === 0) {
          this.accountsSvc.load();
        }
      }),
      switchMap(({ id, asOf }) => {
        if (!id) {
          this.loading.set(false);
          return of(null);
        }
        return this.tbSvc.get(asOf).pipe(
          tap(() => this.loading.set(false)),
          catchError((e: unknown) => {
            this.error.set(
              (e as { message?: string })?.message ?? 'Error loading trial balance',
            );
            this.loading.set(false);
            return of(null);
          }),
        );
      }),
    ),
    { initialValue: null as TrialBalanceResponse | null },
  );

  // Display rows: join each account id with its label, then route balance to Dr or Cr column.
  readonly rows = computed<DisplayRow[]>(() => {
    const tb = this.result();
    if (!tb) return [];
    return tb.accounts.map(row => ({
      accountId: row.accountId,
      label: this.accountsSvc.label(row.accountId),
      debit:
        row.balance > 0
          ? formatMoney(row.balance, 'USD', DEFAULT_FORMAT_PROFILE)
          : null,
      credit:
        row.balance < 0
          ? formatMoney(-row.balance, 'USD', DEFAULT_FORMAT_PROFILE)
          : null,
    }));
  });

  // Column footings (debit-positive convention).
  private readonly debitSum = computed(() => {
    const tb = this.result();
    if (!tb) return 0;
    return tb.accounts.filter(r => r.balance > 0).reduce((s, r) => s + r.balance, 0);
  });

  private readonly creditSum = computed(() => {
    const tb = this.result();
    if (!tb) return 0;
    return tb.accounts.filter(r => r.balance < 0).reduce((s, r) => s + -r.balance, 0);
  });

  readonly debitTotal = computed(() => {
    const tb = this.result();
    if (!tb) return null;
    return formatMoney(this.debitSum(), 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true });
  });

  readonly creditTotal = computed(() => {
    const tb = this.result();
    if (!tb) return null;
    return formatMoney(this.creditSum(), 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true });
  });

  // Out of balance when debits and credits don't foot equal (tolerance for floating point).
  readonly outOfBalance = computed(() => {
    const tb = this.result();
    if (!tb || tb.accounts.length === 0) return false;
    return Math.abs(this.debitSum() - this.creditSum()) > 0.005;
  });

  onAsOfChange(e: Event): void {
    this.asOf.set((e.target as HTMLInputElement).value);
  }
}
