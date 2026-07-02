import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { Payment } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CustomerSelect } from '../../shared/customer-select';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-payment-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Payments</h1>
        <app-customer-select />
        <a *appCan="'ar.write'" hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/payments/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          Record payment
        </a>
      </div>

      @if (svc.customers().length === 0) {
        <p class="text-muted-foreground text-sm">No customers yet — <a routerLink="/receivables/customers" class="underline">add one first</a>.</p>
      } @else if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view payments.</p>
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
export class PaymentList {
  readonly svc = inject(ReceivablesService);
  readonly customerId = this.svc.selectedCustomerId;
  readonly listError = signal<string | null>(null);

  readonly payments = toSignal(
    toObservable(this.customerId).pipe(
      switchMap(cid => {
        if (!cid) return of([] as Payment[]);
        this.listError.set(null);
        return this.svc.listPayments(cid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as Payment[]); }),
        );
      }),
    ),
    { initialValue: [] as Payment[] },
  );

  constructor() { this.svc.load(); }

  allocated(p: Payment): number { return p.allocations.reduce((s, a) => s + a.amount, 0); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
