import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { form, applyEach, validate, validateTree, required, FormField } from '@angular/forms/signals';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { EntriesService } from '../../core/entries/entries.service';
import { PostEntryRequest, PostLineRequest } from '../../core/entries/entry';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { formatMoney } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';

interface LineModel { accountId: string; debit: number | null; credit: number | null; }
interface EntryFormValue { effectiveDate: string; reference: string; memo: string; type: 'Standard' | 'Adjusting'; lines: LineModel[]; }

const emptyLine = (): LineModel => ({ accountId: '', debit: null, credit: null });

@Component({
  selector: 'app-entry-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormField, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-4xl">
      <h1 class="text-2xl font-bold">Post Journal Entry</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Effective date</label>
          <input hlmInput type="date" [formField]="entryForm.effectiveDate" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Type</label>
          <div hlmSelect [value]="entryForm.type().value()" (valueChange)="entryForm.type().value.set($any($event))">
            <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
            <hlm-select-content>
              <hlm-select-item value="Standard">Standard</hlm-select-item>
              <hlm-select-item value="Adjusting">Adjusting</hlm-select-item>
            </hlm-select-content>
          </div>
        </div>
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Reference</label>
        <input hlmInput type="text" [formField]="entryForm.reference" />
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Memo</label>
        <input hlmInput type="text" [formField]="entryForm.memo" />
      </div>

      <table class="w-full text-sm">
        <thead>
          <tr class="text-left text-muted-foreground">
            <th class="py-1">Account</th><th class="text-right">Debit</th><th class="text-right">Credit</th><th></th>
          </tr>
        </thead>
        <tbody>
          @for (line of model().lines; track $index) {
            <tr>
              <td class="py-1 pr-2">
                <div hlmSelect [value]="line.accountId" (valueChange)="setAccount($index, $any($event))" class="w-full">
                  <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
                  <hlm-select-content>
                    @for (a of postableAccounts(); track a.id) {
                      <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item>
                    }
                  </hlm-select-content>
                </div>
              </td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [formField]="entryForm.lines[$index].debit" /></td>
              <td class="pr-2"><input hlmInput type="number" class="text-right tabular-nums" [formField]="entryForm.lines[$index].credit" /></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine($index)" [disabled]="model().lines.length <= 2">✕</button></td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr class="font-semibold border-t border-border">
            <td class="py-1 text-right pr-2">Totals</td>
            <td class="text-right tabular-nums">{{ money(totalDebit()) }}</td>
            <td class="text-right tabular-nums">{{ money(totalCredit()) }}</td>
            <td></td>
          </tr>
        </tfoot>
      </table>

      <div class="flex items-center gap-3">
        <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>
        @if (balanceError()) { <span class="text-destructive text-sm">{{ balanceError() }}</span> }
      </div>

      @if (serverMessage()) {
        <p [class]="serverOk() ? 'text-sm text-[color:var(--brand-teal)]' : 'text-destructive text-sm'">{{ serverMessage() }}</p>
      }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" variant="outline" (click)="validate()" [disabled]="busy()">Validate</button>
        <button hlmBtn type="button" (click)="post()" [disabled]="!canPost() || busy()">Post</button>
      </div>
    </div>
  `,
})
export class EntryForm {
  private readonly entries = inject(EntriesService);
  private readonly accounts = inject(AccountsService);
  private readonly router = inject(Router);

  readonly model = signal<EntryFormValue>({
    effectiveDate: new Date().toISOString().slice(0, 10),
    reference: '', memo: '', type: 'Standard', lines: [emptyLine(), emptyLine()],
  });

  readonly entryForm = form(this.model, (p) => {
    required(p.effectiveDate);
    applyEach(p.lines, (line) => {
      required(line.accountId);
      validate(line, ({ value }) => {
        const l = value(); const d = (l.debit ?? 0) > 0; const c = (l.credit ?? 0) > 0;
        return d === c ? { kind: 'one-side', message: 'Enter a debit OR a credit' } : undefined;
      });
    });
    validateTree(p.lines, ({ value }) => {
      const lines = value();
      const filled = lines.filter(l => (l.debit ?? 0) > 0 || (l.credit ?? 0) > 0).length;
      const totD = lines.reduce((s, l) => s + (l.debit ?? 0), 0);
      const totC = lines.reduce((s, l) => s + (l.credit ?? 0), 0);
      const errs: { kind: string; message: string }[] = [];
      if (filled < 2) errs.push({ kind: 'min-lines', message: 'At least two lines are required' });
      if (Math.round((totD - totC) * 100) !== 0) errs.push({ kind: 'unbalanced', message: 'Debits must equal credits' });
      return errs.length ? errs : undefined;
    });
  });

  readonly busy = signal(false);
  readonly serverMessage = signal<string | null>(null);
  readonly serverOk = signal(false);

  readonly postableAccounts = computed(() => this.accounts.accounts().filter(a => a.postable));
  readonly totalDebit = computed(() => this.model().lines.reduce((s, l) => s + (l.debit ?? 0), 0));
  readonly totalCredit = computed(() => this.model().lines.reduce((s, l) => s + (l.credit ?? 0), 0));
  readonly canPost = computed(() => this.entryForm().valid());
  readonly balanceError = computed(() => this.entryForm.lines().errors().map(e => e.message).filter(Boolean).join('; ') || null);

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  setAccount(i: number, id: string): void { this.entryForm.lines[i].accountId().value.set(id); }
  addLine(): void { this.model.update(v => ({ ...v, lines: [...v.lines, emptyLine()] })); }
  removeLine(i: number): void { this.model.update(v => ({ ...v, lines: v.lines.filter((_, idx) => idx !== i) })); }
  money(n: number): string { return formatMoney(n, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false }); }

  private toRequest(): PostEntryRequest {
    const v = this.model();
    const lines: PostLineRequest[] = v.lines
      .filter(l => (l.debit ?? 0) > 0 || (l.credit ?? 0) > 0)
      .map(l => ({ accountId: l.accountId, direction: (l.debit ?? 0) > 0 ? 'Debit' : 'Credit', amount: (l.debit ?? 0) > 0 ? l.debit! : l.credit! }));
    return { effectiveDate: v.effectiveDate, reference: v.reference || null, memo: v.memo || null, type: v.type, lines };
  }

  validate(): void {
    this.busy.set(true); this.serverMessage.set(null);
    this.entries.validate(this.toRequest()).subscribe({
      next: () => { this.serverOk.set(true); this.serverMessage.set('Entry is valid and would post.'); this.busy.set(false); },
      error: (e) => { this.serverOk.set(false); this.serverMessage.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  post(): void {
    if (!this.canPost()) return;
    this.busy.set(true); this.serverMessage.set(null);
    this.entries.post(this.toRequest()).subscribe({
      next: (r) => { this.router.navigate(['/journal', r.id]); },
      error: (e) => { this.serverOk.set(false); this.serverMessage.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
