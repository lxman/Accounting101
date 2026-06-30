import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { AllocRow, autoAllocate } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-payment-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Invoices</a>
      <h1 class="text-2xl font-bold">Record payment</h1>
      <p class="text-sm text-muted-foreground">{{ svc.customerName(customerId!) }}</p>
      @if (creditBalance() > 0) {
        <p class="text-sm text-muted-foreground">Existing customer credit: {{ money(creditBalance()) }}</p>
      }

      <div class="grid grid-cols-3 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Amount received</label>
          <app-currency-input ariaLabel="Amount received" [value]="amount()" (valueChange)="onAmount($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Method</label>
          <input hlmInput type="text" placeholder="check, card…" [value]="method()" (input)="method.set($any($event.target).value)" />
        </div>
      </div>

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open invoices for this customer — the full amount becomes customer credit.</p>
      } @else {
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-muted-foreground">
              <th class="py-1">Invoice</th><th>Issued</th>
              <th class="text-right pr-5">Open</th><th class="text-right pr-5">Apply</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.invoiceId; let i = $index) {
              <tr>
                <td class="py-1">{{ r.number ?? '—' }}</td>
                <td>{{ formatDate(r.issueDate) }}</td>
                <td class="text-right tabular-nums pr-5">{{ money(r.openBalance) }}</td>
                <td class="pr-2">
                  <div class="flex justify-end">
                    <app-currency-input class="w-32" [ariaLabel]="'Apply to ' + (r.number ?? r.invoiceId)"
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
          <span>Unallocated → customer credit</span><span>{{ money(unallocated()) }}</span>
        </div>
      </div>

      <p class="text-xs text-muted-foreground">
        Recording a payment posts a cash entry that needs approval before it affects the statements.
        The invoice's open balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Record payment</button>
        <a hlmBtn variant="outline" routerLink="/receivables">Cancel</a>
      </div>
    </div>
  `,
})
export class PaymentEditor {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly customerId = this.route.snapshot.queryParamMap.get('customer');
  private readonly focusInvoice = this.route.snapshot.queryParamMap.get('invoice');

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
    if (!this.customerId) { void this.router.navigate(['/receivables']); return; }
    this.svc.load();
    this.svc.listInvoices({ customerId: this.customerId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .subscribe(page => {
        let rows: AllocRow[] = page.items.map(v => ({
          invoiceId: v.invoice.id, number: v.invoice.number, issueDate: v.invoice.issueDate,
          openBalance: v.openBalance, allocation: 0,
        }));
        if (this.focusInvoice) {
          rows = [...rows.filter(r => r.invoiceId === this.focusInvoice), ...rows.filter(r => r.invoiceId !== this.focusInvoice)];
        }
        const initialAmount = this.focusInvoice
          ? (rows.find(r => r.invoiceId === this.focusInvoice)?.openBalance ?? 0) : 0;
        this.amount.set(initialAmount);
        this.rows.set(autoAllocate(initialAmount, rows));
      });
    this.svc.creditBalance(this.customerId).subscribe(b => this.creditBalance.set(b));
  }

  onAmount(v: number): void { this.amount.set(v); this.rows.update(rs => autoAllocate(v, rs)); }
  onRow(i: number, v: number): void { this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, allocation: v } : r)); }

  save(): void {
    if (!this.valid() || !this.customerId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordPayment({
      customerId: this.customerId, date: this.date(), amount: this.amount(),
      method: this.method().trim() || null,
      allocations: this.rows().filter(r => r.allocation > 0).map(r => ({ targetId: r.invoiceId, amount: r.allocation })),
    }).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/receivables']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
