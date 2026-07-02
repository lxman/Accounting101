import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { netPay } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-run-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Record payroll run</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Gross</label>
          <app-currency-input ariaLabel="Gross" [value]="gross()" (valueChange)="gross.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Pay date</label>
          <input hlmInput type="date" [value]="payDate()" (change)="payDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Employee FICA</label>
          <app-currency-input ariaLabel="Employee FICA" [value]="employeeFica()" (valueChange)="employeeFica.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Employer FICA</label>
          <app-currency-input ariaLabel="Employer FICA" [value]="employerFica()" (valueChange)="employerFica.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Income tax withheld</label>
          <app-currency-input ariaLabel="Income tax withheld" [value]="incomeTaxWithheld()" (valueChange)="incomeTaxWithheld.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Deductions</label>
          <app-currency-input ariaLabel="Deductions" [value]="deductions()" (valueChange)="deductions.set($event)" />
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      <div class="text-right text-sm tabular-nums flex justify-between w-64 ms-auto font-semibold border-t border-border pt-1">
        <span>Net pay</span><span [class.text-destructive]="net() < 0">{{ money(net()) }}</span>
      </div>

      @if (netPayWarning()) { <p class="text-destructive text-sm">{{ netPayWarning() }}</p> }
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'payroll.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record payroll run</button>
        <a hlmBtn variant="outline" routerLink="/payroll/runs">Cancel</a>
      </div>
    </div>
  `,
})
export class RunEditor {
  private readonly svc = inject(PayrollService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly gross = signal(0);
  readonly employeeFica = signal(0);
  readonly employerFica = signal(0);
  readonly deductions = signal(0);
  readonly incomeTaxWithheld = signal(0);
  readonly payDate = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly net = computed(() => netPay({
    gross: this.gross(), employeeFica: this.employeeFica(),
    incomeTaxWithheld: this.incomeTaxWithheld(), deductions: this.deductions(),
  }));

  readonly netPayWarning = computed<string | null>(() =>
    this.net() < 0
      ? `Net pay is negative (${fmtMoney(this.net())}) — gross must cover FICA, withholding, and deductions.`
      : null);

  readonly canSave = computed(() => this.gross() > 0 && this.net() >= 0 && !!this.payDate());

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordRun({
      gross: this.gross(), employeeFica: this.employeeFica(), employerFica: this.employerFica(),
      deductions: this.deductions(), incomeTaxWithheld: this.incomeTaxWithheld(),
      payDate: this.payDate(), memo: this.memo(),
    }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (run) => { this.busy.set(false); void this.router.navigate(['/payroll/runs', run.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
}
