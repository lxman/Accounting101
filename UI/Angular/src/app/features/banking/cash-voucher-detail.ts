import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { BankingService } from '../../core/banking/banking.service';
import { CashDisbursement, CashKind } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-cash-voucher-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/cash/cash" class="text-sm text-muted-foreground hover:text-foreground">← Cash vouchers</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (voucher(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ kind() === 'deposit' ? 'Deposit' : 'Disbursement' }} {{ v.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="v.status === 'Void'">{{ v.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Date</td><td class="text-right">{{ date(v.date) }}</td></tr>
            @if (v.reference) { <tr><td class="py-1 text-muted-foreground">Reference</td><td class="text-right">{{ v.reference }}</td></tr> }
            @if (v.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ v.memo }}</td></tr> }
          </tbody>
        </table>

        <table class="w-full text-sm">
          <thead><tr class="text-left text-muted-foreground"><th class="py-1">Account</th><th class="text-right">Amount</th></tr></thead>
          <tbody>
            @for (l of v.lines; track l.accountId) {
              <tr><td class="py-1">{{ label(l.accountId) }}</td><td class="text-right tabular-nums">{{ money(l.amount) }}</td></tr>
            }
            <tr class="font-semibold border-t border-border">
              <td class="py-1 text-right">Cash {{ kind() === 'deposit' ? 'debit' : 'credit' }}</td>
              <td class="text-right tabular-nums">{{ money(total(v)) }}</td></tr>
          </tbody>
        </table>

        @if (postedEntryId(); as eid) { <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a> }

        @if (v.status === 'Posted') {
          <div *appCan="'cash.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)" [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="voidVoucher()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class CashVoucherDetail {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly voucher = signal<CashDisbursement | null>(null);
  readonly kind = signal<CashKind>('disbursement');
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); this.reload(); }

  reload(): void {
    // Try disbursement first; on 404 fall through to deposit.
    this.svc.getDisbursement(this.id).pipe(
      catchError(() => { this.kind.set('deposit'); return this.svc.getDeposit(this.id).pipe(catchError((e) => { this.message.set(extractProblem(e).detail); return of(null); })); }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((v) => { if (v) this.voucher.set(v); this.busy.set(false); });

    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  voidVoucher(): void {
    this.busy.set(true); this.message.set(null);
    const call = this.kind() === 'deposit' ? this.svc.voidDeposit(this.id, this.reason()) : this.svc.voidDisbursement(this.id, this.reason());
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  total(v: CashDisbursement): number { return v.lines.reduce((s, l) => s + l.amount, 0); }
  label(id: string): string { return this.accounts.label(id); }
  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
