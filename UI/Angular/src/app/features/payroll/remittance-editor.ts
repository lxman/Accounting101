import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { remittanceTotal } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-remittance-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Record tax remittance</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Withholdings amount</label>
          <app-currency-input ariaLabel="Withholdings amount" [value]="withholdingsAmount()" (valueChange)="withholdingsAmount.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Taxes amount</label>
          <app-currency-input ariaLabel="Taxes amount" [value]="taxesAmount()" (valueChange)="taxesAmount.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Pay date</label>
          <input hlmInput type="date" [value]="payDate()" (change)="payDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      <div class="text-right text-sm tabular-nums flex justify-between w-64 ms-auto font-semibold border-t border-border pt-1">
        <span>Total</span><span>{{ money(total()) }}</span>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record remittance</button>
        <a hlmBtn variant="outline" routerLink="/payroll/remittances">Cancel</a>
      </div>
    </div>
  `,
})
export class RemittanceEditor {
  private readonly svc = inject(PayrollService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly withholdingsAmount = signal(0);
  readonly taxesAmount = signal(0);
  readonly payDate = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly total = computed(() => remittanceTotal({ withholdingsAmount: this.withholdingsAmount(), taxesAmount: this.taxesAmount() }));
  readonly canSave = computed(() => this.total() > 0 && !!this.payDate());

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordRemittance({
      withholdingsAmount: this.withholdingsAmount(), taxesAmount: this.taxesAmount(),
      payDate: this.payDate(), memo: this.memo(),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (m) => { this.busy.set(false); void this.router.navigate(['/payroll/remittances', m.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
}
