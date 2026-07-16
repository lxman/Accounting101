import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { InvoiceAllocationLine, PaymentView } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-payment-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/payments" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Payments</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Payment</h1>
          <span class="text-sm px-2 py-0.5 rounded border border-border">{{ v.payment.voided ? 'Voided' : 'Active' }}</span>
        </div>
        <div class="text-sm flex flex-col gap-1">
          <div><span class="text-muted-foreground">Date</span> · {{ formatDate(v.payment.date) }}</div>
          <div><span class="text-muted-foreground">Amount</span> · <span class="tabular-nums">{{ money(v.payment.amount) }}</span></div>
          <div><span class="text-muted-foreground">Method</span> · {{ v.payment.method ?? '—' }}</div>
        </div>

        <div class="flex flex-col gap-1">
          <h2 class="text-sm font-semibold">Applied to</h2>
          @if (v.allocations.length === 0) {
            <p class="text-muted-foreground text-sm">No allocations.</p>
          } @else {
            <table class="text-sm w-full max-w-md">
              <tbody>
                @for (a of v.allocations; track a.invoiceId) {
                  <tr>
                    <td class="py-0.5">Invoice {{ a.invoiceNumber ?? '—' }}</td>
                    <td class="py-0.5 text-right tabular-nums">{{ money(a.amount) }}</td>
                  </tr>
                }
                <tr class="border-t border-border font-semibold">
                  <td class="py-0.5">Total applied</td>
                  <td class="py-0.5 text-right tabular-nums">{{ money(sum(v.allocations)) }}</td>
                </tr>
              </tbody>
            </table>
          }
          <div class="text-sm mt-1"><span class="text-muted-foreground">Unapplied (held as customer credit)</span> · <span class="tabular-nums">{{ money(v.unapplied) }}</span></div>
        </div>

        @if (v.journalEntryId) {
          <a *appCan="'gl.read'" [routerLink]="['/journal', v.journalEntryId]" class="text-sm text-primary hover:underline w-fit">View journal entry →</a>
        }
      } @else if (loadError()) {
        <p class="text-destructive text-sm">{{ loadError() }}</p>
      } @else {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }
    </div>
  `,
})
export class PaymentDetail {
  private readonly svc = inject(ReceivablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<PaymentView | null>(null);
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.svc.getPayment(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.loadError.set(extractProblem(e).detail),
    });
  }

  sum(lines: InvoiceAllocationLine[]): number { return lines.reduce((s, a) => s + a.amount, 0); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
