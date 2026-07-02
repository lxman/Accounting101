import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';
import { PayablesService } from '../../core/payables/payables.service';
import { BillView, SettlementFilter, billTotal } from '../../core/payables/payables';
import { PagedResponse } from '../../core/api/paged-response';
import { money, displayDate } from '../../core/format/display';
import { VendorSelect } from '../../shared/vendor-select';
import { SettlementBadge } from '../../shared/settlement-badge';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-bill-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, VendorSelect, SettlementBadge, CanDirective, ...HlmSelectImports, ...HlmTableImports, ...HlmPaginationImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Bills</h1>
        <app-vendor-select />
        <div hlmSelect [value]="settlement()" (valueChange)="onSettlementChange($event)" [itemToString]="settlementToLabel">
          <hlm-select-trigger class="w-36"><hlm-select-value placeholder="All" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">All</hlm-select-item>
            <hlm-select-item value="open">Open</hlm-select-item>
            <hlm-select-item value="paid">Paid</hlm-select-item>
          </hlm-select-content>
        </div>
        <a *appCan="'ap.write'" hlmBtn size="sm" class="ms-auto" routerLink="/payables/bills/new"
           [class.pointer-events-none]="!vendorId()" [class.opacity-50]="!vendorId()">New bill</a>
      </div>

      @if (svc.vendors().length === 0) {
        <p class="text-muted-foreground text-sm">No vendors yet — <a routerLink="/payables/vendors" class="underline">add one first</a>.</p>
      } @else if (!vendorId()) {
        <p class="text-muted-foreground text-sm">Select a vendor to view bills.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (bills().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No bills found.</p>
        } @else {
          <div hlmTableContainer><table hlmTable>
            <thead hlmTHead><tr hlmTr>
              <th hlmTh>Number</th><th hlmTh>Bill date</th><th hlmTh>Due</th>
              <th hlmTh>Vendor ref</th><th hlmTh>Total</th><th hlmTh>Open</th><th hlmTh>Status</th>
            </tr></thead>
            <tbody hlmTBody>
              @for (v of bills(); track v.bill.id) {
                <tr hlmTr role="button" class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="openBill(v.bill.id)" (keydown.enter)="openBill(v.bill.id)">
                  <td hlmTd>{{ v.bill.number ?? '—' }}</td>
                  <td hlmTd>{{ fmtDate(v.bill.billDate) }}</td>
                  <td hlmTd>{{ v.bill.dueDate ? fmtDate(v.bill.dueDate) : '—' }}</td>
                  <td hlmTd>{{ v.bill.vendorReference ?? '—' }}</td>
                  <td hlmTd>{{ fmtMoney(calcTotal(v)) }}</td>
                  <td hlmTd>{{ fmtMoney(v.openBalance) }}</td>
                  <td hlmTd class="flex gap-1 flex-wrap items-center">
                    <span class="text-xs text-muted-foreground">{{ v.bill.status }}</span>
                    <app-settlement-badge [status]="v.settlementStatus" />
                  </td>
                </tr>
              }
            </tbody>
          </table></div>

          <div class="flex items-center justify-between text-sm text-muted-foreground">
            <span class="whitespace-nowrap">Page {{ currentPage() }} of {{ pageCount() }}</span>
            <nav hlmPagination aria-label="Bills pagination"><ul hlmPaginationContent>
              <li hlmPaginationItem><hlm-pagination-previous
                [class]="skip() === 0 ? 'pointer-events-none opacity-50' : ''" (click)="prevPage()" /></li>
              <li hlmPaginationItem><hlm-pagination-next
                [class]="currentPage() >= pageCount() ? 'pointer-events-none opacity-50' : ''" (click)="nextPage()" /></li>
            </ul></nav>
          </div>
        }
      }
    </div>
  `,
})
export class BillList {
  readonly svc = inject(PayablesService);
  private readonly router = inject(Router);

  readonly vendorId = this.svc.selectedVendorId;
  readonly settlement = signal<SettlementFilter | ''>('');
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly listError = signal<string | null>(null);

  private readonly query = computed(() => ({
    vendorId: this.vendorId(), settlement: this.settlement() || undefined, skip: this.skip(), limit: this.limit(),
  }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      switchMap(q => {
        if (!q.vendorId) return of(null);
        this.listError.set(null);
        return this.svc.listBills({
          vendorId: q.vendorId, settlement: q.settlement as SettlementFilter | undefined, skip: q.skip, limit: q.limit,
        }).pipe(catchError(e => { this.listError.set(extractProblem(e).detail); return of(null); }));
      }),
    ),
    { initialValue: null as PagedResponse<BillView> | null },
  );

  readonly bills = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => {
    const p = this.page(); if (!p || p.total === 0) return 1; return Math.ceil(p.total / p.limit);
  });
  readonly currentPage = computed(() => {
    const p = this.page(); if (!p) return 1; return Math.floor(p.skip / p.limit) + 1;
  });

  readonly settlementToLabel = (v: string): string => v === 'open' ? 'Open' : v === 'paid' ? 'Paid' : 'All';

  constructor() {
    this.svc.load();
    effect(() => { this.vendorId(); this.skip.set(0); });
  }

  onSettlementChange(value: unknown): void {
    this.settlement.set((value as string ?? '') as SettlementFilter | '');
    this.skip.set(0);
  }

  openBill(id: string): void { void this.router.navigate(['/payables/bills', id]); }

  prevPage(): void { const s = this.skip(), l = this.limit(); if (s > 0) this.skip.set(Math.max(0, s - l)); }
  nextPage(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }

  calcTotal(v: BillView): number { return billTotal(v.bill.lines); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
