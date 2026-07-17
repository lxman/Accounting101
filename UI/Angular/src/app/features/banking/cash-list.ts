import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { BankingService } from '../../core/banking/banking.service';
import { CashVoucherRow } from '../../core/banking/banking';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { PaginationPrefsService } from '../../core/pagination/pagination-prefs.service';
import { Paginator } from '../../shared/paginator';
import { TruncateDirective } from '../../shared/truncate.directive';

@Component({
  selector: 'app-cash-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports, Paginator, TruncateDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 flex-1 min-h-0">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Cash vouchers</h1>
        <div class="ms-auto flex gap-2">
          <a *appCan="'cash.write'" hlmBtn size="sm" variant="outline" routerLink="/cash/cash/deposits/new">New deposit</a>
          <a *appCan="'cash.write'" hlmBtn size="sm" routerLink="/cash/cash/disbursements/new">New disbursement</a>
        </div>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (rows().length === 0) {
        <p class="text-muted-foreground text-sm">No cash vouchers yet.</p>
      } @else {
        <div hlmTableContainer class="flex-1 min-h-0 overflow-y-auto">
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr><th hlmTh>Number</th><th hlmTh>Date</th><th hlmTh>Type</th>
                <th hlmTh class="text-right">Amount</th><th hlmTh>Memo</th><th hlmTh>Status</th></tr>
            </thead>
            <tbody hlmTBody>
              @for (r of rows(); track r.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(r.id)" (keydown.enter)="open(r.id)">
                  <td hlmTd>{{ r.number ?? '—' }}</td>
                  <td hlmTd>{{ date(r.date) }}</td>
                  <td hlmTd>{{ r.kind === 'deposit' ? 'Deposit' : 'Disbursement' }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="r.kind === 'disbursement'">
                    {{ r.kind === 'disbursement' ? '(' + money(r.amount) + ')' : money(r.amount) }}</td>
                  <td hlmTd><span appTruncate>{{ r.memo ?? '' }}</span></td>
                  <td hlmTd [class.text-destructive]="r.status === 'Void'">{{ r.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Cash vouchers pagination" (previous)="prev()" (next)="next()" [pageSize]="limit()" (pageSizeChange)="setPageSize($event)" />
      }
    </div>
  `,
})
export class CashList {
  private readonly svc = inject(BankingService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);
  private readonly prefs = inject(PaginationPrefsService);

  readonly skip = signal(0);
  readonly limit = this.prefs.pageSize;
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listCash({ skip, limit }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading cash vouchers'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<CashVoucherRow> | null },
  );

  readonly rows = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  open(id: string): void { void this.router.navigate(['/cash/cash', id]); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  setPageSize(n: number): void { this.prefs.setPageSize(n); this.skip.set(0); }
  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
