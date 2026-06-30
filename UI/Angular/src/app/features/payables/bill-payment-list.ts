import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayablesService } from '../../core/payables/payables.service';
import { BillPayment } from '../../core/payables/payables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { VendorSelect } from '../../shared/vendor-select';

@Component({
  selector: 'app-bill-payment-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, VendorSelect],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Payments</h1>
        <app-vendor-select />
        <a hlmBtn size="sm" class="ms-auto"
           routerLink="/payables/payments/new"
           [queryParams]="{ vendor: vendorId() }"
           [class.pointer-events-none]="!vendorId()"
           [class.opacity-50]="!vendorId()">
          Record payment
        </a>
      </div>

      @if (svc.vendors().length === 0) {
        <p class="text-muted-foreground text-sm">No vendors yet — <a routerLink="/payables/vendors" class="underline">add one first</a>.</p>
      } @else if (!vendorId()) {
        <p class="text-muted-foreground text-sm">Select a vendor to view payments.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (payments().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No payments recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Date</th><th hlmTh>Amount</th><th hlmTh>Method</th>
                  <th hlmTh>Allocated</th><th hlmTh>Unapplied</th><th hlmTh>Status</th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (p of payments(); track p.id) {
                  <tr hlmTr [class.opacity-50]="p.voided">
                    <td hlmTd>{{ fmtDate(p.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(p.amount) }}</td>
                    <td hlmTd>{{ p.method ?? '—' }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(allocated(p)) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(p.amount - allocated(p)) }}</td>
                    <td hlmTd>{{ p.voided ? 'Voided' : 'Active' }}</td>
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
export class BillPaymentList {
  readonly svc = inject(PayablesService);
  readonly vendorId = this.svc.selectedVendorId;
  readonly listError = signal<string | null>(null);

  readonly payments = toSignal(
    toObservable(this.vendorId).pipe(
      switchMap(vid => {
        if (!vid) return of([] as BillPayment[]);
        this.listError.set(null);
        return this.svc.listBillPayments(vid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as BillPayment[]); }),
        );
      }),
    ),
    { initialValue: [] as BillPayment[] },
  );

  constructor() { this.svc.load(); }

  allocated(p: BillPayment): number { return p.allocations.reduce((s, a) => s + a.amount, 0); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
