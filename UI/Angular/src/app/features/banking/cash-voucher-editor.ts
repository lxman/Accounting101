import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { CashKind, CashLine, RecordCashVoucherRequest } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

interface LineModel { lineId: string; accountId: string; amount: number | null; }
const emptyLine = (): LineModel => ({ lineId: crypto.randomUUID(), accountId: '', amount: null });

@Component({
  selector: 'app-cash-voucher-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">{{ isDeposit() ? 'Record cash deposit' : 'Record cash disbursement' }}</h1>
      <p class="text-sm text-muted-foreground">
        Enter the non-cash lines. The balancing Cash {{ isDeposit() ? 'debit' : 'credit' }} is posted automatically.
      </p>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Date</label>
          <input hlmInput type="date" [value]="date()" (change)="date.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Reference</label>
          <input hlmInput type="text" [value]="reference() ?? ''" (input)="reference.set($any($event.target).value || null)" />
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      <table class="w-full text-sm">
        <thead><tr class="text-left text-muted-foreground"><th class="py-1">Account</th><th class="text-right">Amount</th><th></th></tr></thead>
        <tbody>
          @for (line of lines(); track line.lineId; let i = $index) {
            <tr>
              <td class="py-1 pr-2">
                <div hlmSelect [value]="line.accountId" [itemToString]="accountItemToString" (valueChange)="setAccount(i, $any($event))" class="w-full">
                  <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
                  <hlm-select-content *hlmSelectPortal>
                    @for (a of postableAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
                  </hlm-select-content>
                </div>
              </td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [value]="line.amount ?? ''"
                    (input)="setAmount(i, $any($event.target).value === '' ? null : +$any($event.target).value)" /></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)" [disabled]="lines().length <= 1">✕</button></td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr class="font-semibold border-t border-border">
            <td class="py-1 text-right pr-2">Cash {{ isDeposit() ? 'debit' : 'credit' }} (auto)</td>
            <td class="text-right tabular-nums">{{ money(total()) }}</td><td></td>
          </tr>
        </tfoot>
      </table>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-3">
        <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>
        <div class="flex items-center gap-2 ms-auto">
          <button *appCan="'cash.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record</button>
          <a hlmBtn variant="outline" routerLink="/cash/cash">Cancel</a>
        </div>
      </div>
    </div>
  `,
})
export class CashVoucherEditor {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly kind = (this.route.snapshot.data['kind'] as CashKind) ?? 'disbursement';
  readonly isDeposit = computed(() => this.kind === 'deposit');

  readonly date = signal(new Date().toISOString().slice(0, 10));
  readonly reference = signal<string | null>(null);
  readonly memo = signal<string | null>(null);
  readonly lines = signal<LineModel[]>([emptyLine()]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly postableAccounts = computed(() => this.accounts.accounts().filter(a => a.postable));
  readonly total = computed(() => this.lines().reduce((s, l) => s + (l.amount ?? 0), 0));
  readonly canSave = computed(() =>
    this.lines().length > 0 && this.lines().every(l => l.accountId && (l.amount ?? 0) > 0));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  setAccount(i: number, id: string): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, accountId: id } : l)); }
  setAmount(i: number, amount: number | null): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, amount } : l)); }
  addLine(): void { this.lines.update(v => [...v, emptyLine()]); }
  removeLine(i: number): void { this.lines.update(v => v.filter((_, idx) => idx !== i)); }
  money(n: number): string { return fmtMoney(n); }

  private toRequest(): RecordCashVoucherRequest {
    const lines: CashLine[] = this.lines().map(l => ({ accountId: l.accountId, amount: l.amount! }));
    return { lines, date: this.date(), reference: this.reference(), memo: this.memo() };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    const call = this.isDeposit() ? this.svc.recordDeposit(this.toRequest()) : this.svc.recordDisbursement(this.toRequest());
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.busy.set(false); void this.router.navigate(['/cash/cash', v.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
