import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayrollService } from '../../core/payroll/payroll.service';
import { TaxRemittance, remittanceTotal } from '../../core/payroll/payroll';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { PaginationPrefsService } from '../../core/pagination/pagination-prefs.service';
import { Paginator } from '../../shared/paginator';

@Component({
  selector: 'app-remittance-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports, Paginator],
  template: `
    <div class="flex flex-col gap-4 p-4 flex-1 min-h-0">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Tax remittances</h1>
        <a *appCan="'payroll.write'" hlmBtn size="sm" routerLink="/payroll/remittances/new" class="ms-auto">Record remittance</a>
        <label class="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" />
          Show voided
        </label>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (remittances().length === 0) {
        <p class="text-muted-foreground text-sm">No tax remittances yet.</p>
      } @else {
        <div hlmTableContainer class="flex-1 min-h-0 overflow-y-auto">
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>#</th><th hlmTh>Pay date</th>
                <th hlmTh class="text-right">Withholdings</th><th hlmTh class="text-right">Taxes</th>
                <th hlmTh class="text-right">Total</th><th hlmTh>Status</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (m of remittances(); track m.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(m.id)" (keydown.enter)="open(m.id)">
                  <td hlmTd>{{ m.number ?? '—' }}</td>
                  <td hlmTd>{{ formatDate(m.payDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(m.withholdingsAmount) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(m.taxesAmount) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(total(m)) }}</td>
                  <td hlmTd>{{ m.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Tax remittances pagination" (previous)="prev()" (next)="next()" [pageSize]="limit()" (pageSizeChange)="setPageSize($event)" />
      }
    </div>
  `,
})
export class RemittanceList {
  private readonly svc = inject(PayrollService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);
  private readonly prefs = inject(PaginationPrefsService);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = this.prefs.pageSize;
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({
    id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit(),
  }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, includeVoided, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listRemittances({ skip, limit, includeVoided }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading remittances'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<TaxRemittance> | null },
  );

  readonly remittances = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  total(m: TaxRemittance): number { return remittanceTotal(m); }
  open(id: string): void { void this.router.navigate(['/payroll/remittances', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  setPageSize(n: number): void { this.prefs.setPageSize(n); this.skip.set(0); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
