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
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-vendor-credit-apply-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">Apply vendor credit</h1>
      <p class="text-sm text-muted-foreground">{{ svc.vendorName(vendorId!) }}</p>
      <p class="text-sm text-muted-foreground">Available credit: <span class="tabular-nums font-semibold text-foreground">{{ money(available()) }}</span></p>

      <div class="grid grid-cols-2 gap-4 max-w-sm">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
      </div>

      @if (rows().length === 0) {
        <p class="text-sm text-muted-foreground">No open bills for this vendor — nothing to apply credit to.</p>
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
        <div class="flex justify-between" [class.text-destructive]="allocated() > available()">
          <span>Remaining credit</span><span>{{ money(remaining()) }}</span>
        </div>
      </div>

      <p class="text-xs text-muted-foreground">
        Applying credit posts an entry that needs approval before it affects the statements.
        The bill's open balance updates immediately.
      </p>

      @if (allocationWarning()) { <p class="text-destructive text-sm">{{ allocationWarning() }}</p> }

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'ap.write'" hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Apply credit</button>
        <a hlmBtn variant="outline" routerLink="/payables/credits">Cancel</a>
      </div>
    </div>
  `,
})
export class VendorCreditApplyEditor {
  readonly svc = inject(PayablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly vendorId = this.route.snapshot.queryParamMap.get('vendor');

  readonly available = signal(0);
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly rows = signal<AllocRow[]>([]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly allocated = computed(() => Math.round(this.rows().reduce((s, r) => s + r.allocation, 0) * 100) / 100);
  readonly remaining = computed(() => Math.round((this.available() - this.allocated()) * 100) / 100);
  readonly valid = computed(() =>
    this.allocated() > 0 &&
    this.rows().every(r => r.allocation >= 0 && r.allocation <= r.openBalance) &&
    this.allocated() <= this.available());
  readonly allocationWarning = computed<string | null>(() => {
    const over = Math.round((this.allocated() - this.available()) * 100) / 100;
    if (over > 0)
      return `Applied ${this.money(this.allocated())} exceeds available credit by ${this.money(over)}.`;
    if (this.rows().some(r => r.allocation > r.openBalance))
      return 'A line is applied more than its open balance.';
    return null;
  });

  constructor() {
    if (!this.vendorId) { void this.router.navigate(['/payables']); return; }
    this.svc.load();
    this.svc.vendorCreditBalance(this.vendorId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(b => {
      this.available.set(b);
      this.rows.update(rs => autoAllocate(b, rs));
    });
    this.svc.listBills({ vendorId: this.vendorId, settlement: 'open', skip: 0, limit: 200, order: 'asc' })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(page => {
        const rows: AllocRow[] = page.items.map(v => ({
          billId: v.bill.id, number: v.bill.number, billDate: v.bill.billDate,
          openBalance: v.openBalance, allocation: 0,
        }));
        this.rows.set(autoAllocate(this.available(), rows));
      });
  }

  onRow(i: number, v: number): void { this.rows.update(rs => rs.map((r, idx) => idx === i ? { ...r, allocation: v } : r)); }

  save(): void {
    if (!this.valid() || !this.vendorId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.applyVendorCredit({
      vendorId: this.vendorId, date: this.date(),
      allocations: this.rows().filter(r => r.allocation > 0).map(r => ({ targetId: r.billId, amount: r.allocation })),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/payables/credits']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
