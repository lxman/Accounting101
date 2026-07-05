import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatementLine, RecordBankStatementRequest } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

interface LineModel extends BankStatementLine { lineId: string; }
const emptyLine = (): LineModel => ({ lineId: crypto.randomUUID(), date: new Date().toISOString().slice(0, 10), amount: 0, description: '', externalRef: null });

@Component({
  selector: 'app-statement-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <h1 class="text-2xl font-bold">New bank statement</h1>
      <p class="text-sm text-muted-foreground">Line amounts are signed from the bank's view: + money in, − money out. The statement must foot before you can record it.</p>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Cash account</label>
          <div hlmSelect [value]="cashAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="cashAccountId.set($any($event))" class="w-full">
            <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a cash account" /></hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal>
              @for (a of cashAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
            </hlm-select-content>
          </div>
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Statement date</label>
          <input hlmInput type="date" [value]="statementDate()" (change)="statementDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Opening balance</label>
          <input hlmInput type="number" class="text-right tabular-nums" [value]="openingBalance()" (input)="openingBalance.set(+$any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Closing balance</label>
          <input hlmInput type="number" class="text-right tabular-nums" [value]="closingBalance()" (input)="closingBalance.set(+$any($event.target).value)" />
        </div>
      </div>

      <table class="w-full text-sm">
        <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Description</th><th class="text-right">Amount</th><th>Ref</th><th></th></tr></thead>
        <tbody>
          @for (l of lines(); track l.lineId; let i = $index) {
            <tr>
              <td class="py-1 pr-2"><input hlmInput type="date" [value]="l.date" (change)="patch(i, { date: $any($event.target).value })" /></td>
              <td class="pr-2"><input hlmInput type="text" [value]="l.description" (input)="patch(i, { description: $any($event.target).value })" /></td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [value]="l.amount" (input)="patch(i, { amount: +$any($event.target).value })" /></td>
              <td class="pr-2"><input hlmInput type="text" [value]="l.externalRef ?? ''" (input)="patch(i, { externalRef: $any($event.target).value || null })" /></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)" [disabled]="lines().length <= 1">✕</button></td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr class="border-t border-border">
            <td class="py-1 text-right pr-2" colspan="2">Opening + lines</td>
            <td class="text-right tabular-nums">{{ money(computedClosing()) }}</td>
            <td colspan="2" [class.text-destructive]="!foots()" [class.text-emerald-600]="foots()">{{ foots() ? 'Foots' : 'Off by ' + money(difference()) }}</td>
          </tr>
        </tfoot>
      </table>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-3">
        <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>
        <div class="flex items-center gap-2 ms-auto">
          <button *appCan="'bankrec.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record statement</button>
          <a hlmBtn variant="outline" routerLink="/cash/statements">Cancel</a>
        </div>
      </div>
    </div>
  `,
})
export class StatementEditor {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly cashAccountId = signal<string | null>(null);
  readonly statementDate = signal(new Date().toISOString().slice(0, 10));
  readonly openingBalance = signal(0);
  readonly closingBalance = signal(0);
  readonly lines = signal<LineModel[]>([emptyLine()]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));
  readonly lineSum = computed(() => this.lines().reduce((s, l) => s + (l.amount ?? 0), 0));
  readonly computedClosing = computed(() => this.openingBalance() + this.lineSum());
  readonly difference = computed(() => Math.round((this.computedClosing() - this.closingBalance()) * 100) / 100);
  readonly foots = computed(() => this.difference() === 0);
  readonly canSave = computed(() =>
    !!this.cashAccountId() && this.foots() && this.lines().every(l => l.date && l.description));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  patch(i: number, part: Partial<BankStatementLine>): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, ...part } : l)); }
  setLine(i: number, line: BankStatementLine): void { this.lines.update(v => v.map((l, idx) => idx === i ? { ...l, ...line } : l)); }
  addLine(): void { this.lines.update(v => [...v, emptyLine()]); }
  removeLine(i: number): void { this.lines.update(v => v.filter((_, idx) => idx !== i)); }
  money(n: number): string { return fmtMoney(n); }

  private toRequest(): RecordBankStatementRequest {
    return { cashAccountId: this.cashAccountId()!, statementDate: this.statementDate(),
      openingBalance: this.openingBalance(), closingBalance: this.closingBalance(),
      lines: this.lines().map(({ lineId, ...l }) => l) };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordStatement(this.toRequest()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => { this.busy.set(false); void this.router.navigate(['/cash/statements', s.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
