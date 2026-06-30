import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { CreditType } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

interface AdjustRow {
  invoiceId: string;
  number: string | null;
  issueDate: string;
  openBalance: number;
  included: boolean;
  amount: number;
}

@Component({
  selector: 'app-adjustment-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/credits" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Credits</a>
      <h1 class="text-2xl font-bold">Record adjustment</h1>
      <p class="text-sm text-muted-foreground">{{ svc.customerName(customerId!) }}</p>

      <div class="flex gap-4 flex-wrap">
        @for (opt of types; track opt.value) {
          <label class="flex items-center gap-2 text-sm">
            <input type="radio" name="type" [value]="opt.value" [checked]="type() === opt.value"
                   (change)="setType(opt.value)" [attr.aria-label]="opt.label" />
            {{ opt.label }}
          </label>
        }
      </div>

      <div class="grid grid-cols-2 gap-4 max-w-md">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        @if (type() !== 'credit-application') {
          <div class="flex flex-col gap-1">
            <label hlmLabel>Memo</label>
            <input hlmInput type="text" placeholder="reason…" [value]="memo()" (input)="memo.set($any($event.target).value)" />
          </div>
        }
      </div>

      @if (type() === 'credit-application') {
        <p class="text-sm" [class.text-destructive]="total() > creditBalance()">
          Available credit {{ money(creditBalance()) }}
        </p>
      }

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open invoices to adjust.</p>
      } @else {
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-muted-foreground">
              <th class="py-1"></th><th>Invoice</th><th>Issued</th>
              <th class="text-right pr-5">Open</th><th class="text-right pr-5">Amount</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.invoiceId; let i = $index) {
              <tr>
                <td class="py-1">
                  <input type="checkbox" [checked]="r.included" (change)="toggleRow(i, $any($event.target).checked)"
                         [attr.aria-label]="'Include ' + (r.number ?? r.invoiceId)" />
                </td>
                <td>{{ r.number ?? '—' }}</td>
                <td>{{ formatDate(r.issueDate) }}</td>
                <td class="text-right tabular-nums pr-5">{{ money(r.openBalance) }}</td>
                <td class="pr-2">
                  <div class="flex justify-end">
                    <app-currency-input class="w-32" [ariaLabel]="'Amount for ' + (r.number ?? r.invoiceId)"
                         [value]="r.amount" (valueChange)="setAmount(i, $event)" />
                  </div>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }

      <div class="text-right text-sm tabular-nums w-72 ms-auto flex justify-between">
        <span>Total</span><span>{{ money(total()) }}</span>
      </div>

      <p class="text-xs text-muted-foreground">
        Recording an adjustment posts an entry that needs approval before it affects the statements.
        The invoice's open balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Record adjustment</button>
        <a hlmBtn variant="outline" routerLink="/receivables/credits">Cancel</a>
      </div>
    </div>
  `,
})
export class AdjustmentEditor {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly customerId = this.route.snapshot.queryParamMap.get('customer');
  readonly types: { value: CreditType; label: string }[] = [
    { value: 'credit-note', label: 'Credit note' },
    { value: 'write-off', label: 'Write-off' },
    { value: 'credit-application', label: 'Apply credit' },
  ];

  readonly type = signal<CreditType>('credit-note');
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal('');
  readonly rows = signal<AdjustRow[]>([]);
  readonly creditBalance = signal(0);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly total = computed(() =>
    Math.round(this.rows().filter(r => r.included).reduce((s, r) => s + r.amount, 0) * 100) / 100);

  readonly valid = computed(() => {
    const included = this.rows().filter(r => r.included);
    if (included.length === 0) return false;
    if (!included.every(r => r.amount > 0 && r.amount <= r.openBalance)) return false;
    if (this.type() === 'credit-application' && this.total() > this.creditBalance()) return false;
    return true;
  });

  constructor() {
    if (!this.customerId) { void this.router.navigate(['/receivables/credits']); return; }
    this.svc.load();
    this.svc.listInvoices({ customerId: this.customerId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .subscribe(page => this.rows.set(page.items.map(v => ({
        invoiceId: v.invoice.id,
        number: v.invoice.number,
        issueDate: v.invoice.issueDate,
        openBalance: v.openBalance,
        included: false,
        amount: 0,
      }))));
    this.svc.creditBalance(this.customerId).subscribe(b => this.creditBalance.set(b));
  }

  setType(t: CreditType): void { this.type.set(t); }

  toggleRow(i: number, included: boolean): void {
    this.rows.update(rs => rs.map((r, idx) => idx === i
      ? { ...r, included, amount: included ? r.openBalance : 0 } : r));
  }

  setAmount(i: number, v: number): void {
    this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, amount: v } : r));
  }

  save(): void {
    if (!this.valid() || !this.customerId) return;
    this.busy.set(true);
    this.message.set(null);
    const allocations = this.rows()
      .filter(r => r.included && r.amount > 0)
      .map(r => ({ targetId: r.invoiceId, amount: r.amount }));
    const customerId = this.customerId;
    const date = this.date();
    const memo = this.memo().trim() || null;

    const done = {
      next: () => { this.busy.set(false); void this.router.navigate(['/receivables/credits']); },
      error: (e: unknown) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    };

    switch (this.type()) {
      case 'credit-note':
        this.svc.recordCreditNote({ customerId, date, allocations, memo }).subscribe(done);
        break;
      case 'write-off':
        this.svc.recordWriteOff({ customerId, date, allocations, memo }).subscribe(done);
        break;
      case 'credit-application':
        this.svc.applyCredit({ customerId, date, allocations }).subscribe(done);
        break;
    }
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
