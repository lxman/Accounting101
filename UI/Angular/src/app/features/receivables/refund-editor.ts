import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

@Component({
  selector: 'app-refund-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">Issue refund</h1>
      <p class="text-sm text-muted-foreground">{{ svc.customerName(customerId!) }}</p>
      <p class="text-sm" [class.text-destructive]="amount() > creditBalance()">
        Available credit {{ money(creditBalance()) }}
      </p>

      <div class="grid grid-cols-2 gap-4 max-w-md">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Refund amount</label>
          <app-currency-input ariaLabel="Refund amount" [value]="amount()" (valueChange)="amount.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
      </div>
      <div class="flex flex-col gap-1 max-w-md">
        <label hlmLabel>Memo</label>
        <input hlmInput type="text" placeholder="reason…" [value]="memo()" (input)="memo.set($any($event.target).value)" />
      </div>

      <p class="text-xs text-muted-foreground">
        Issuing a refund posts a cash entry that needs approval before it affects the statements.
        The customer's credit balance updates immediately.
      </p>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!valid() || busy()">Issue refund</button>
        <a hlmBtn variant="outline" routerLink="/receivables/refunds">Cancel</a>
      </div>
    </div>
  `,
})
export class RefundEditor {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly customerId = this.route.snapshot.queryParamMap.get('customer');

  readonly amount = signal(0);
  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal('');
  readonly creditBalance = signal(0);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly valid = computed(() => this.amount() > 0 && this.amount() <= this.creditBalance());

  constructor() {
    if (!this.customerId) { void this.router.navigate(['/receivables/refunds']); return; }
    this.svc.load();
    this.svc.creditBalance(this.customerId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(b => {
      this.creditBalance.set(b);
      this.amount.set(b);                 // default to the full available credit
    });
  }

  save(): void {
    if (!this.valid() || !this.customerId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordRefund({ customerId: this.customerId, date: this.date(), amount: this.amount(), memo: this.memo().trim() || null })
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: () => { this.busy.set(false); void this.router.navigate(['/receivables/refunds']); },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }

  money(n: number): string { return fmtMoney(n); }
}
