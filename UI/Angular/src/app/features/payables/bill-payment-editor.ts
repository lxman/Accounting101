import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayablesService } from '../../core/payables/payables.service';
import { AllocRow, autoAllocate } from '../../core/payables/payables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-bill-payment-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">Record payment</h1>
      <p class="text-sm text-muted-foreground">{{ svc.vendorName(vendorId!) }}</p>
      @if (creditBalance() > 0) {
        <p class="text-sm text-muted-foreground">Existing vendor credit: {{ money(creditBalance()) }}</p>
      }

      <div class="grid grid-cols-3 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Amount paid</label>
          <app-currency-input ariaLabel="Amount paid" [value]="amount()" (valueChange)="onAmount($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Method</label>
          <input hlmInput type="text" placeholder="check, ACH…" [value]="method()" (input)="method.set($any($event.target).value)" />
        </div>
      </div>

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open bills for this vendor — the full amount becomes vendor credit.</p>
      } @else {
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-muted-foreground">
              <th class="py-1">Bill</th><th>Bill date</th>
              <th class="text-right pr-5">Open</th><th class="text-right pr-5">Apply</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.billId; let i = $index) {
              <tr>
                <td class="py-1">{{ r.number ?? '—' }}</td>
                <td>{{ formatDate(r.billDate) }}</td>
                <td class="text-right tabular-nums pr-5">{{ money(r.openBalance) }}</td>
                <td class="pr-2">
                  <div class="flex justify-end">
                    <app-currency-input class="w-32" [ariaLabel]="'Apply to ' + (r.number ?? r.billId)"
                         [value]="r.allocation" (valueChange)="onRow(i, $event)" />
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }

      <div class="text-right text-sm tabular-nums flex flex-col gap-1 w-72 ms-auto">
        <div class="flex justify-between"><span>Allocated</span><span>{{ money(allocated()) }}</span></div>
        <div class="flex justify-between" [class.text-destructive]="allocated() > amount()">
          <span>Unallocated → vendor credit</span><span>{{ money(unallocated()) }}</span>
        </div>
      </div>

      <p class="text-xs text-muted-foreground">
        Recording a payment posts a cash entry that needs approval before it affects the statements.
        The bill's open balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Record payment</button>
        <a hlmBtn variant="outline" routerLink="/payables/payments">Cancel</a>
      </div>
    </div>
  `,
})
export class BillPaymentEditor {
  readonly svc = inject(PayablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly vendorId = this.route.snapshot.queryParamMap.get('vendor');
  private readonly focusBill = this.route.snapshot.queryParamMap.get('bill');

  readonly amount = signal(0);
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly method = signal('');
  readonly rows = signal<AllocRow[]>([]);
  readonly creditBalance = signal(0);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly allocated = computed(() => Math.round(this.rows().reduce((s, r) => s + r.allocation, 0) * 100) / 100);
  readonly unallocated = computed(() => Math.max(0, Math.round((this.amount() - this.allocated()) * 100) / 100));
  readonly valid = computed(() =>
    this.amount() > 0 &&
    this.rows().every(r => r.allocation >= 0 && r.allocation <= r.openBalance) &&
    this.allocated() <= this.amount());

  constructor() {
    if (!this.vendorId) { void this.router.navigate(['/payables']); return; }
    this.svc.load();
    this.svc.listBills({ vendorId: this.vendorId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(page => {
        let rows: AllocRow[] = page.items.map(v => ({
          billId: v.bill.id, number: v.bill.number, billDate: v.bill.billDate,
          openBalance: v.openBalance, allocation: 0,
        }));
        if (this.focusBill) {
          rows = [...rows.filter(r => r.billId === this.focusBill), ...rows.filter(r => r.billId !== this.focusBill)];
        }
        const initialAmount = this.focusBill
          ? (rows.find(r => r.billId === this.focusBill)?.openBalance ?? 0) : 0;
        this.amount.set(initialAmount);
        this.rows.set(autoAllocate(initialAmount, rows));
      });
    this.svc.vendorCreditBalance(this.vendorId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(b => this.creditBalance.set(b));
  }

  onAmount(v: number): void { this.amount.set(v); this.rows.update(rs => autoAllocate(v, rs)); }
  onRow(i: number, v: number): void { this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, allocation: v } : r)); }

  save(): void {
    if (!this.valid() || !this.vendorId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordBillPayment({
      vendorId: this.vendorId, date: this.date(), amount: this.amount(),
      method: this.method().trim() || null,
      allocations: this.rows().filter(r => r.allocation > 0).map(r => ({ targetId: r.billId, amount: r.allocation })),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/payables/payments']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
