import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import {
  InterchangeFormat, CsvMapping, ColumnRef, StatementPreview, BankStatementLine, RecordBankStatementRequest,
} from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

type MappableField = 'date' | 'amount' | 'debit' | 'credit' | 'description' | 'reference';
/** An editable preview: server lines + the user-supplied balances/date that the preview may not carry. */
interface EditablePreview { lines: BankStatementLine[]; openingBalance: number; closingBalance: number; statementDate: string; createdId?: string; error?: string; }
type Stage = 'upload' | 'preview';

@Component({
  selector: 'app-statement-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/cash/statements" class="text-sm text-muted-foreground hover:text-foreground">← Statements</a>
      <h1 class="text-2xl font-bold">Import bank statement</h1>

      @if (stage() === 'upload') {
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
            <label hlmLabel>Format</label>
            <div hlmSelect [value]="format()" (valueChange)="format.set($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                <hlm-select-item value="Csv">CSV</hlm-select-item>
                <hlm-select-item value="Ofx">OFX</hlm-select-item>
              </hlm-select-content>
            </div>
          </div>
          <div class="flex flex-col gap-1 col-span-2">
            <label hlmLabel>File</label>
            <input hlmInput type="file" (change)="onFile($event)" />
          </div>
        </div>

        @if (format() === 'Csv') {
          <div class="flex flex-col gap-3 border border-border rounded p-3">
            <div class="flex items-center gap-2">
              <input type="checkbox" [checked]="hasHeader()" (change)="hasHeader.set($any($event.target).checked)" id="hdr" />
              <label for="hdr" class="text-sm">File has a header row</label>
            </div>
            <p class="text-xs text-muted-foreground">Enter the zero-based column index for each field. Use Amount for a single signed column, or Debit + Credit for a two-column layout.</p>
            <div class="grid grid-cols-3 gap-3">
              @for (f of fields; track f.key) {
                <div class="flex flex-col gap-1">
                  <label hlmLabel>{{ f.label }}{{ f.required ? ' *' : '' }}</label>
                  <input hlmInput type="number" min="0" [value]="columnIndex(f.key) ?? ''"
                         (input)="setColumn(f.key, $any($event.target).value === '' ? null : +$any($event.target).value)" />
                </div>
              }
              <div class="flex flex-col gap-1">
                <label hlmLabel>Date format (optional)</label>
                <input hlmInput type="text" placeholder="yyyy-MM-dd" [value]="dateFormat() ?? ''" (input)="dateFormat.set($any($event.target).value || null)" />
              </div>
              <div class="flex flex-col gap-1">
                <label hlmLabel>Delimiter (optional)</label>
                <input hlmInput type="text" maxlength="1" placeholder="," [value]="delimiter() ?? ''" (input)="delimiter.set($any($event.target).value || null)" />
              </div>
            </div>
          </div>
        }

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
        <div class="flex items-center gap-2">
          <button *appCan="'bankrec.write'" hlmBtn type="button" (click)="upload()" [disabled]="!canUpload() || busy()">Upload &amp; preview</button>
        </div>
      }

      @if (stage() === 'preview') {
        @if (warnings().length) {
          <div class="border border-amber-400 rounded p-3 text-sm">
            <p class="font-semibold">Warnings</p>
            <ul class="list-disc ps-5">@for (w of warnings(); track $index) { <li>{{ w }}</li> }</ul>
          </div>
        }
        @for (p of previews(); track $index; let i = $index) {
          <div class="border border-border rounded p-3 flex flex-col gap-3">
            <div class="flex items-center gap-3">
              <h2 class="font-semibold">Statement {{ i + 1 }} — {{ p.lines.length }} lines</h2>
              @if (p.createdId) { <a [routerLink]="['/cash/statements', p.createdId]" class="text-primary hover:underline text-sm ms-auto">Recorded →</a> }
            </div>
            <div class="grid grid-cols-3 gap-3">
              <div class="flex flex-col gap-1"><label hlmLabel>Statement date</label>
                <input hlmInput type="date" [value]="p.statementDate" (change)="patchPreview(i, { statementDate: $any($event.target).value })" [disabled]="!!p.createdId" /></div>
              <div class="flex flex-col gap-1"><label hlmLabel>Opening</label>
                <input hlmInput type="number" class="text-right tabular-nums" [value]="p.openingBalance" (input)="patchPreview(i, { openingBalance: +$any($event.target).value })" [disabled]="!!p.createdId" /></div>
              <div class="flex flex-col gap-1"><label hlmLabel>Closing</label>
                <input hlmInput type="number" class="text-right tabular-nums" [value]="p.closingBalance" (input)="patchPreview(i, { closingBalance: +$any($event.target).value })" [disabled]="!!p.createdId" /></div>
            </div>
            <table class="w-full text-sm">
              <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Description</th><th class="text-right">Amount</th></tr></thead>
              <tbody>@for (l of p.lines; track $index) {
                <tr><td class="py-1">{{ l.date }}</td><td class="break-words">{{ l.description }}</td><td class="text-right tabular-nums">{{ money(l.amount) }}</td></tr> }</tbody>
            </table>
            <p class="text-xs" [class.text-destructive]="!foots(p)" [class.text-emerald-600]="foots(p)">{{ foots(p) ? 'Foots' : 'Does not foot — adjust balances' }}</p>
            @if (p.error) { <p class="text-destructive text-sm">{{ p.error }}</p> }
            @if (!p.createdId) {
              <button *appCan="'bankrec.write'" hlmBtn size="sm" type="button" (click)="confirm(i)" [disabled]="!foots(p) || busy()">Record statement</button>
            }
          </div>
        }
        <button hlmBtn variant="outline" type="button" (click)="reset()">Import another</button>
      }
    </div>
  `,
})
export class StatementImport {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly fields: { key: MappableField; label: string; required: boolean }[] = [
    { key: 'date', label: 'Date', required: true }, { key: 'amount', label: 'Amount', required: false },
    { key: 'debit', label: 'Debit', required: false }, { key: 'credit', label: 'Credit', required: false },
    { key: 'description', label: 'Description', required: true }, { key: 'reference', label: 'Reference', required: false },
  ];

  readonly stage = signal<Stage>('upload');
  readonly cashAccountId = signal<string | null>(null);
  readonly file = signal<File | null>(null);
  readonly format = signal<InterchangeFormat>('Csv');
  readonly hasHeader = signal(true);
  readonly dateFormat = signal<string | null>(null);
  readonly delimiter = signal<string | null>(null);
  private readonly columns = signal<Partial<Record<MappableField, number | null>>>({});
  readonly previews = signal<EditablePreview[]>([]);
  readonly warnings = signal<string[]>([]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));
  readonly canUpload = computed(() => {
    if (!this.cashAccountId() || !this.file()) return false;
    if (this.format() === 'Ofx') return true;
    const c = this.columns();
    const hasAmount = c.amount != null || (c.debit != null && c.credit != null);
    return c.date != null && c.description != null && hasAmount;
  });

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  onFile(e: Event): void { this.file.set((e.target as HTMLInputElement).files?.[0] ?? null); }
  columnIndex(f: MappableField): number | null { return this.columns()[f] ?? null; }
  setColumn(f: MappableField, index: number | null): void { this.columns.update(c => ({ ...c, [f]: index })); }
  money(n: number): string { return fmtMoney(n); }
  foots(p: EditablePreview): boolean {
    const sum = p.lines.reduce((s, l) => s + l.amount, 0);
    return Math.round((p.openingBalance + sum - p.closingBalance) * 100) === 0;
  }
  patchPreview(i: number, part: Partial<EditablePreview>): void { this.previews.update(v => v.map((p, idx) => idx === i ? { ...p, ...part } : p)); }
  reset(): void { this.stage.set('upload'); this.previews.set([]); this.warnings.set([]); this.file.set(null); this.message.set(null); }

  private buildMapping(): CsvMapping | null {
    if (this.format() !== 'Csv') return null;
    const c = this.columns();
    const ref = (n?: number | null): ColumnRef | null => (n == null ? null : { index: n });
    return {
      date: ref(c.date)!, amount: ref(c.amount), debit: ref(c.debit), credit: ref(c.credit),
      description: ref(c.description)!, reference: ref(c.reference),
      dateFormat: this.dateFormat(), hasHeader: this.hasHeader(),
      delimiter: this.delimiter(), status: null, excludeStatuses: null,
    };
  }

  upload(): void {
    if (!this.canUpload()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.importStatements(this.file()!, this.format(), this.buildMapping())
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (res) => {
          this.previews.set(res.statements.map((s: StatementPreview) => ({
            lines: s.lines,
            openingBalance: s.detectedOpeningBalance ?? 0,
            closingBalance: s.detectedClosingBalance ?? 0,
            statementDate: s.statementDate ?? new Date().toISOString().slice(0, 10),
          })));
          this.warnings.set(res.warnings);
          this.stage.set('preview'); this.busy.set(false);
        },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }

  confirm(i: number): void {
    const p = this.previews()[i];
    if (!this.foots(p)) return;
    this.busy.set(true); this.patchPreview(i, { error: undefined });
    const req: RecordBankStatementRequest = {
      cashAccountId: this.cashAccountId()!, statementDate: p.statementDate,
      openingBalance: p.openingBalance, closingBalance: p.closingBalance, lines: p.lines,
    };
    this.svc.recordStatement(req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => { this.patchPreview(i, { createdId: s.id }); this.busy.set(false); },
      error: (e) => { this.patchPreview(i, { error: extractProblem(e).detail }); this.busy.set(false); },
    });
  }
}
