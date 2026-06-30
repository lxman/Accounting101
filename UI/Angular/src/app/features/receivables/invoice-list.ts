import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import {
  InvoiceView,
  SettlementFilter,
  invoiceTotals,
} from '../../core/receivables/receivables';
import { PagedResponse } from '../../core/api/paged-response';
import { money, displayDate } from '../../core/format/display';
import { InvoiceStatusBadge } from '../../shared/invoice-status-badge';
import { SettlementBadge } from '../../shared/settlement-badge';

@Component({
  selector: 'app-invoice-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    HlmButton,
    InvoiceStatusBadge,
    SettlementBadge,
    ...HlmSelectImports,
    ...HlmTableImports,
    ...HlmPaginationImports,
  ],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Invoices</h1>

        <div hlmSelect [value]="customerId()" (valueChange)="onCustomerChange($event)" [itemToString]="toCustomerName">
          <hlm-select-trigger class="w-64">
            <hlm-select-value placeholder="Select a customer" />
          </hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (c of svc.customers(); track c.id) {
              <hlm-select-item [value]="c.id">{{ c.name }}</hlm-select-item>
            }
          </hlm-select-content>
        </div>

        <div hlmSelect [value]="settlement()" (valueChange)="onSettlementChange($event)">
          <hlm-select-trigger class="w-36">
            <hlm-select-value placeholder="All" />
          </hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">All</hlm-select-item>
            <hlm-select-item value="open">Open</hlm-select-item>
            <hlm-select-item value="paid">Paid</hlm-select-item>
          </hlm-select-content>
        </div>

        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/invoices/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          New invoice
        </a>
      </div>

      @if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view invoices.</p>
      } @else {
        @if (invoices().length === 0) {
          <p class="text-muted-foreground text-sm">No invoices found.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Number</th>
                  <th hlmTh>Issue</th>
                  <th hlmTh>Due</th>
                  <th hlmTh>Total</th>
                  <th hlmTh>Open</th>
                  <th hlmTh>Status</th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (v of invoices(); track v.invoice.id) {
                  <tr hlmTr>
                    <td hlmTd>
                      <a [routerLink]="'/receivables/invoices/' + v.invoice.id">
                        {{ v.invoice.number ?? '—' }}
                      </a>
                    </td>
                    <td hlmTd>{{ fmtDate(v.invoice.issueDate) }}</td>
                    <td hlmTd>{{ v.invoice.dueDate ? fmtDate(v.invoice.dueDate) : '—' }}</td>
                    <td hlmTd>{{ fmtMoney(calcTotal(v)) }}</td>
                    <td hlmTd>{{ fmtMoney(v.openBalance) }}</td>
                    <td hlmTd class="flex gap-1 flex-wrap">
                      <app-invoice-status-badge [status]="v.invoice.status" />
                      <app-settlement-badge [status]="v.settlementStatus" />
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <div class="flex items-center justify-between text-sm text-muted-foreground">
            <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
            <nav hlmPagination aria-label="Invoices pagination">
              <ul hlmPaginationContent>
                <li hlmPaginationItem>
                  <hlm-pagination-previous
                    [class]="skip() === 0 ? 'pointer-events-none opacity-50' : ''"
                    (click)="prevPage()"
                  />
                </li>
                <li hlmPaginationItem>
                  <hlm-pagination-next
                    [class]="currentPage() >= pageCount() ? 'pointer-events-none opacity-50' : ''"
                    (click)="nextPage()"
                  />
                </li>
              </ul>
            </nav>
          </div>
        }
      }
    </div>
  `,
})
export class InvoiceList {
  readonly svc = inject(ReceivablesService);

  readonly customerId = signal('');
  readonly settlement = signal<SettlementFilter | ''>('');
  readonly skip = signal(0);
  readonly limit = signal(50);

  // Mirrors entry-list: computed query re-emits when any filter/page signal changes;
  // switchMap cancels in-flight request so a stale response can never overwrite newer data.
  private readonly query = computed(() => ({
    customerId: this.customerId(),
    settlement: this.settlement() || undefined,
    skip: this.skip(),
    limit: this.limit(),
  }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      switchMap(q => {
        if (!q.customerId) return of(null);
        return this.svc.listInvoices({
          customerId: q.customerId,
          settlement: q.settlement as SettlementFilter | undefined,
          skip: q.skip,
          limit: q.limit,
        });
      }),
    ),
    { initialValue: null as PagedResponse<InvoiceView> | null },
  );

  readonly invoices = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => {
    const p = this.page();
    if (!p || p.total === 0) return 1;
    return Math.ceil(p.total / p.limit);
  });
  readonly currentPage = computed(() => {
    const p = this.page();
    if (!p) return 1;
    return Math.floor(p.skip / p.limit) + 1;
  });

  /** Passed to [itemToString] so the trigger shows the customer name rather than the raw GUID. */
  readonly toCustomerName = (id: string): string => this.svc.customerName(id);

  constructor() { this.svc.load(); }

  onCustomerChange(value: unknown): void {
    this.customerId.set(value as string ?? '');
    this.skip.set(0);
  }

  onSettlementChange(value: unknown): void {
    this.settlement.set((value as string ?? '') as SettlementFilter | '');
    this.skip.set(0);
  }

  prevPage(): void {
    const s = this.skip();
    const l = this.limit();
    if (s > 0) this.skip.set(Math.max(0, s - l));
  }

  nextPage(): void {
    if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit());
  }

  calcTotal(v: InvoiceView): number {
    return invoiceTotals(v.invoice.lines, v.invoice.taxRate).total;
  }

  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
