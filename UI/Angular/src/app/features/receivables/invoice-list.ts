import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import {
  InvoiceView,
  SettlementFilter,
  invoiceTotals,
} from '../../core/receivables/receivables';
import { PagedResponse } from '../../core/api/paged-response';
import { money, displayDate } from '../../core/format/display';
import { CustomerSelect } from '../../shared/customer-select';
import { InvoiceStatusBadge } from '../../shared/invoice-status-badge';
import { SettlementBadge } from '../../shared/settlement-badge';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';
import { Paginator } from '../../shared/paginator';

@Component({
  selector: 'app-invoice-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    HlmButton,
    CustomerSelect,
    InvoiceStatusBadge,
    SettlementBadge,
    CanDirective,
    ...HlmSelectImports,
    ...HlmTableImports,
    Paginator,
  ],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Invoices</h1>

        <app-customer-select />

        <div hlmSelect [value]="settlement()" (valueChange)="onSettlementChange($event)" [itemToString]="settlementToLabel">
          <hlm-select-trigger class="w-36">
            <hlm-select-value placeholder="All" />
          </hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">All</hlm-select-item>
            <hlm-select-item value="open">Open</hlm-select-item>
            <hlm-select-item value="paid">Paid</hlm-select-item>
          </hlm-select-content>
        </div>

        <a *appCan="'ar.write'" hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/invoices/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          New invoice
        </a>
      </div>

      @if (svc.customers().length === 0) {
        <p class="text-muted-foreground text-sm">No customers yet — <a routerLink="/receivables/customers" class="underline">add one first</a>.</p>
      } @else if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view invoices.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (invoices().length === 0 && !listError()) {
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
                  <tr hlmTr class="cursor-pointer hover:bg-muted/50"
                      tabindex="0"
                      (click)="openInvoice(v.invoice.id)"
                      (keydown.enter)="openInvoice(v.invoice.id)">
                    <td hlmTd>{{ v.invoice.number ?? '—' }}</td>
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

          <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Invoices pagination" (previous)="prevPage()" (next)="nextPage()" />
        }
      }
    </div>
  `,
})
export class InvoiceList {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);

  // Selection lives in the service so it survives leaving and returning (and a reload); see ReceivablesService.
  readonly customerId = this.svc.selectedCustomerId;
  readonly settlement = signal<SettlementFilter | ''>('');
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly listError = signal<string | null>(null);

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
        this.listError.set(null);
        return this.svc.listInvoices({
          customerId: q.customerId,
          settlement: q.settlement as SettlementFilter | undefined,
          skip: q.skip,
          limit: q.limit,
        }).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of(null); }),
        );
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

  readonly settlementToLabel = (v: string): string => v === 'open' ? 'Open' : v === 'paid' ? 'Paid' : 'All';

  constructor() {
    this.svc.load();
    // The shared <app-customer-select> writes the selection directly; reset paging to page 1
    // whenever the selected customer changes (a no-op when skip is already 0).
    effect(() => { this.customerId(); this.skip.set(0); });
  }

  onSettlementChange(value: unknown): void {
    this.settlement.set((value as string ?? '') as SettlementFilter | '');
    this.skip.set(0);
  }

  openInvoice(id: string): void { void this.router.navigate(['/receivables/invoices', id]); }

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
