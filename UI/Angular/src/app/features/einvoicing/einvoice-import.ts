import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { EInvoicingService } from '../../core/einvoicing/einvoicing.service';
import { FindingDto, InboundPreviewResponse, matchVendor, profileLabel } from '../../core/einvoicing/einvoicing';
import { PayablesService } from '../../core/payables/payables.service';
import { DraftBillRequest, billTotal } from '../../core/payables/payables';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';
import { CanDirective } from '../../core/capabilities/can.directive';

type Stage = 'upload' | 'preview';

/** One editable proposal line. The document supplies description and amount; the operator supplies
 *  the expense account, which is the one thing a proposal can never carry. */
interface EditableLine { lineId: string; description: string; amount: number; expenseAccountId: string | null; }

/**
 * Upload an EN 16931 e-invoice — a Factur-X/ZUGFeRD PDF, a CII XML, or a UBL XML — review what the
 * engine found, code each line, and post a draft bill through Payables' own API.
 *
 * The e-invoicing module is advisory and stateless: it writes nothing. Everything that turns a
 * proposal into a bill happens here.
 */
@Component({
  selector: 'app-einvoice-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports,
            CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-5xl">
      <a routerLink="/payables/bills" class="text-sm text-muted-foreground hover:text-foreground">← Bills</a>
      <h1 class="text-2xl font-bold">Import e-invoice</h1>

      @if (stage() === 'upload') {
        <p class="text-sm text-muted-foreground max-w-prose">
          Upload a Factur-X / ZUGFeRD PDF, or a CII or UBL XML invoice. Nothing is saved until you
          create the bill.
        </p>
        <div class="flex flex-col gap-1 max-w-md">
          <label hlmLabel>File</label>
          <input hlmInput type="file" accept=".pdf,.xml,application/pdf,text/xml,application/xml"
                 (change)="onFile($event)" />
        </div>
        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
        <div>
          <button hlmBtn type="button" (click)="upload()" [disabled]="!canUpload() || busy()">
            Upload &amp; preview
          </button>
        </div>
      }

      @if (stage() === 'preview') {
        @if (preview(); as p) {
          <div class="flex items-center gap-2 flex-wrap text-sm">
            <span class="font-medium">{{ file()?.name }}</span>
            <span class="rounded border border-border px-2 py-0.5 text-xs">{{ p.syntax }}</span>
            <span class="rounded border border-border px-2 py-0.5 text-xs">{{ profile() }}</span>
          </div>

          @if (errors().length) {
            <div class="border border-destructive rounded p-3 flex flex-col gap-2">
              <p class="font-semibold text-destructive">Errors</p>
              <ul class="flex flex-col gap-1">
                @for (f of errors(); track $index) {
                  <li class="text-sm">
                    <code class="text-xs bg-muted px-1 rounded">{{ f.ruleId }}</code>
                    <span class="ms-1">{{ f.message }}</span>
                    @if (f.businessTerm) {
                      <span class="text-muted-foreground text-xs ms-1">({{ f.businessTerm }})</span>
                    }
                    @if (f.location) { <div class="text-muted-foreground text-xs">{{ f.location }}</div> }
                  </li>
                }
              </ul>
            </div>
          }

          @if (otherCount()) {
            <div class="border border-border rounded p-3 flex flex-col gap-2">
              <button hlmBtn type="button" variant="ghost" size="sm" class="self-start"
                      (click)="showOtherFindings.set(!showOtherFindings())">
                {{ showOtherFindings() ? 'Hide' : 'Show' }} {{ otherCount() }} advisory finding{{ otherCount() === 1 ? '' : 's' }}
              </button>
              @if (showOtherFindings()) {
                <ul class="flex flex-col gap-1">
                  @for (f of advisories(); track $index) {
                    <li class="text-sm">
                      <code class="text-xs bg-muted px-1 rounded">{{ f.ruleId }}</code>
                      <span class="ms-1 text-muted-foreground text-xs">{{ f.severity }}</span>
                      <span class="ms-1">{{ f.message }}</span>
                      @if (f.businessTerm) {
                        <span class="text-muted-foreground text-xs ms-1">({{ f.businessTerm }})</span>
                      }
                    </li>
                  }
                </ul>
              }
            </div>
          }

          @if (!p.canImport) {
            <p class="text-sm text-muted-foreground max-w-prose">
              This document can't become a bill. Fix it with the vendor, or upload a different file.
            </p>
            <div>
              <button hlmBtn type="button" variant="outline" (click)="reset()">Choose another file</button>
            </div>
          } @else if (createdBillId(); as billId) {
            <div class="border border-border rounded p-3 flex flex-col gap-2">
              <p class="font-semibold">Draft bill created</p>
              <a [routerLink]="['/payables/bills', billId]" class="text-primary hover:underline text-sm">
                Open the draft →
              </a>
            </div>
            <div>
              <button hlmBtn type="button" variant="outline" (click)="reset()">Import another</button>
            </div>
          } @else if (proposal(); as prop) {
            <div class="flex flex-col gap-1 max-w-md">
              <label hlmLabel>Vendor</label>
              <div hlmSelect [value]="vendorId()" [itemToString]="vendorLabel"
                   (valueChange)="vendorId.set($any($event))">
                <hlm-select-trigger class="w-full">
                  <hlm-select-value placeholder="Select a vendor" />
                </hlm-select-trigger>
                <hlm-select-content *hlmSelectPortal>
                  @for (v of pay.vendors(); track v.id) {
                    <hlm-select-item [value]="v.id">{{ v.name }}</hlm-select-item>
                  }
                </hlm-select-content>
              </div>
              @if (canCreateVendor()) {
                <p class="text-xs text-muted-foreground">
                  The document's seller is <span class="font-medium">{{ sellerName() }}</span>, which
                  doesn't match an existing vendor.
                </p>
                <button *appCan="'ap.write'" hlmBtn type="button" variant="outline" size="sm"
                        class="self-start" (click)="createVendor()" [disabled]="creatingVendor()">
                  Create vendor "{{ sellerName() }}"
                </button>
              }
            </div>

            <dl class="grid grid-cols-2 gap-x-6 gap-y-1 text-sm max-w-xl">
              <dt class="text-muted-foreground">Bill date</dt><dd>{{ date(prop.billDate) }}</dd>
              <dt class="text-muted-foreground">Due date</dt>
              <dd>{{ prop.dueDate ? date(prop.dueDate) : '—' }}</dd>
              <dt class="text-muted-foreground">Vendor reference</dt><dd>{{ prop.vendorReference ?? '—' }}</dd>
              <dt class="text-muted-foreground">Memo</dt><dd>{{ prop.memo ?? '—' }}</dd>
            </dl>

            @if (expenseAccounts().length === 0) {
              <p class="text-sm text-destructive">
                No postable expense accounts exist yet —
                <a routerLink="/accounts" class="underline">add one first</a>.
              </p>
            }

            @if (!prop.linesAreDetail) {
              <p class="text-xs text-muted-foreground max-w-prose">
                The document's detail lines couldn't be reconciled to its total, so this is a single
                line at the invoice total. Edit the amount and add lines to split it across accounts.
              </p>
            }

            <table class="w-full text-sm">
              <thead><tr class="text-left text-muted-foreground">
                <th class="py-1">Description</th><th class="pr-2">Expense account</th>
                <th class="text-right pr-5">Amount</th><th></th>
              </tr></thead>
              <tbody>
                @for (line of lines(); track line.lineId; let i = $index) {
                  <tr>
                    <td class="py-1 pr-2">
                      <input hlmInput type="text" class="w-full" [value]="line.description"
                             (input)="patchLine(i, { description: $any($event.target).value })" />
                    </td>
                    <td class="pr-2">
                      <div hlmSelect [value]="line.expenseAccountId ?? ''" [itemToString]="accountLabel"
                           (valueChange)="patchLine(i, { expenseAccountId: $any($event) || null })">
                        <hlm-select-trigger class="w-56">
                          <hlm-select-value placeholder="Select account" />
                        </hlm-select-trigger>
                        <hlm-select-content *hlmSelectPortal>
                          @for (a of expenseAccounts(); track a.id) {
                            <hlm-select-item [value]="a.id">{{ a.number }} · {{ a.name }}</hlm-select-item>
                          }
                        </hlm-select-content>
                      </div>
                    </td>
                    <td class="pr-2"><div class="flex justify-end">
                      <app-currency-input class="w-32" ariaLabel="Amount" [value]="line.amount"
                                          (valueChange)="patchLine(i, { amount: $event })" />
                    </div></td>
                    <td>
                      <button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)">✕</button>
                    </td>
                  </tr>
                }
              </tbody>
            </table>

            <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>

            <div class="text-right text-sm tabular-nums flex flex-col gap-1 w-72 ms-auto">
              <div class="flex justify-between"><span>Document total</span><span>{{ money(documentTotal()) }}</span></div>
              <div class="flex justify-between font-semibold border-t border-border pt-1">
                <span>Lines total</span><span>{{ money(linesTotal()) }}</span>
              </div>
              @if (totalDrift() !== 0) {
                <p class="text-amber-600 text-xs text-left">
                  Lines total {{ money(linesTotal()) }}, document says {{ money(documentTotal()) }}.
                </p>
              }
            </div>

            @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

            <div class="flex items-center gap-2">
              <button *appCan="'ap.write'" hlmBtn type="button" (click)="confirm()"
                      [disabled]="!canConfirm() || busy()">Create draft bill</button>
              <button hlmBtn type="button" variant="outline" [disabled]="busy() || creatingVendor()"
                      (click)="reset()">Choose another file</button>
            </div>
          }
        }
      }
    </div>
  `,
})
export class EInvoiceImport {
  private readonly svc = inject(EInvoicingService);
  private readonly destroyRef = inject(DestroyRef);

  /** Bumped by `reset()` only. `confirm()`/`createVendor()` capture it before subscribing and
   *  check it in every callback, so a response that outlives a `reset()` — the operator moved on
   *  to a different document before it arrived — becomes an inert no-op instead of clobbering the
   *  new document's `vendorId`/`createdBillId`. */
  private generation = 0;

  readonly stage = signal<Stage>('upload');
  readonly file = signal<File | null>(null);
  readonly preview = signal<InboundPreviewResponse | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly showOtherFindings = signal(false);

  protected readonly pay = inject(PayablesService);
  private readonly accounts = inject(AccountsService);

  readonly vendorId = signal('');
  readonly lines = signal<EditableLine[]>([]);
  readonly createdBillId = signal<string | null>(null);
  readonly creatingVendor = signal(false);

  readonly expenseAccounts = this.accounts.expenseAccounts;
  readonly proposal = computed(() => this.preview()?.proposal ?? null);
  readonly sellerName = computed(() => this.preview()?.vendorMatch.sellerName ?? '');
  readonly linesTotal = computed(() => billTotal(this.lines().map(l => ({ amount: l.amount }))));
  readonly documentTotal = computed(() => this.proposal()?.total ?? 0);
  readonly totalDrift = computed(() => Math.round((this.linesTotal() - this.documentTotal()) * 100) / 100);
  readonly canCreateVendor = computed(() => !this.vendorId() && !!this.sellerName().trim());

  /** The same predicate Payables enforces server-side: a vendor, at least one line, and every line
   *  carrying a description, an account and a positive amount. */
  readonly canConfirm = computed(() =>
    !!this.vendorId() && this.lines().length > 0 &&
    this.lines().every(l => !!l.description.trim() && !!l.expenseAccountId && l.amount > 0));

  readonly vendorLabel = (id: string): string => this.pay.vendorName(id);
  readonly accountLabel = (id: string): string => this.accounts.label(id);

  constructor() {
    this.pay.load();
    this.accounts.load();
    // Vendors may still be in flight when the preview lands, so match again when they arrive.
    // Only fills an empty selection — never overrides the operator.
    effect(() => {
      const vendors = this.pay.vendors();
      const r = this.preview();
      if (!r || this.vendorId() || vendors.length === 0) return;
      const hit = matchVendor(r.vendorMatch.sellerName, vendors);
      if (hit) this.vendorId.set(hit.id);
    });
  }

  addLine(): void {
    this.lines.update(ls => [...ls,
      { lineId: crypto.randomUUID(), description: '', amount: 0, expenseAccountId: null }]);
  }

  removeLine(i: number): void { this.lines.update(ls => ls.filter((_, idx) => idx !== i)); }

  patchLine(i: number, part: Partial<EditableLine>): void {
    this.lines.update(ls => ls.map((l, idx) => (idx === i ? { ...l, ...part } : l)));
  }

  createVendor(): void {
    const name = this.sellerName().trim();
    if (!name || this.creatingVendor()) return;
    this.creatingVendor.set(true);
    this.message.set(null);
    const gen = this.generation;
    this.pay.create(name, null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: v => { if (gen !== this.generation) return; this.vendorId.set(v.id); this.creatingVendor.set(false); },
      error: e => { if (gen !== this.generation) return; this.message.set(extractProblem(e).detail); this.creatingVendor.set(false); },
    });
  }

  confirm(): void {
    const p = this.proposal();
    if (!p || !this.canConfirm() || this.busy()) return;
    this.busy.set(true);
    this.message.set(null);
    const req: DraftBillRequest = {
      vendorId: this.vendorId(),
      billDate: p.billDate,
      dueDate: p.dueDate,
      vendorReference: p.vendorReference,
      memo: p.memo,
      lines: this.lines().map(l => ({
        description: l.description, amount: l.amount, expenseAccountId: l.expenseAccountId!,
      })),
    };
    const gen = this.generation;
    this.pay.draftBill(req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: b => { if (gen !== this.generation) return; this.createdBillId.set(b.id); this.busy.set(false); },
      error: e => { if (gen !== this.generation) return; this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return displayDate(d); }

  readonly canUpload = computed(() => !!this.file());
  readonly profile = computed(() => profileLabel(this.preview()?.profile ?? null));
  readonly errors = computed(() => this.findingsOf('Error'));
  /** Warnings and information, in that order — advisory, never blocking. */
  readonly advisories = computed(() => [...this.findingsOf('Warning'), ...this.findingsOf('Information')]);
  readonly otherCount = computed(() => this.advisories().length);

  private findingsOf(severity: FindingDto['severity']): FindingDto[] {
    return (this.preview()?.findings ?? []).filter(f => f.severity === severity);
  }

  onFile(e: Event): void {
    const picked = (e.target as HTMLInputElement).files?.[0] ?? null;
    this.file.set(picked);
    this.preview.set(null);
    this.showOtherFindings.set(false);
    this.message.set(picked && picked.size > EInvoicingService.maxUploadBytes ? this.tooLarge() : null);
  }

  upload(): void {
    const f = this.file();
    if (!f || this.busy()) return;
    if (f.size > EInvoicingService.maxUploadBytes) { this.message.set(this.tooLarge()); return; }
    this.busy.set(true);
    this.message.set(null);
    this.svc.previewInbound(f).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => { this.preview.set(r); this.seed(r); this.stage.set('preview'); this.busy.set(false); },
      error: e => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  /** Derive the one editable model from a fresh preview. Header fields stay on the response object
   *  — that is what makes "read-only from the document" structural rather than a disabled input. */
  private seed(r: InboundPreviewResponse): void {
    const p = r.proposal;
    this.lines.set(p ? p.lines.map(l => ({
      lineId: crypto.randomUUID(), description: l.description, amount: l.amount, expenseAccountId: null,
    })) : []);
    this.vendorId.set(p?.suggestedVendorId ?? matchVendor(r.vendorMatch.sellerName, this.pay.vendors())?.id ?? '');
    this.createdBillId.set(null);
  }

  reset(): void {
    // Bumping generation makes any confirm()/createVendor() response already in flight for the
    // document being discarded a no-op wherever it lands — see the field comment above.
    this.generation++;
    this.stage.set('upload');
    this.preview.set(null);
    this.file.set(null);
    this.message.set(null);
    this.showOtherFindings.set(false);
    this.lines.set([]);
    this.vendorId.set('');
    this.createdBillId.set(null);
    this.creatingVendor.set(false);
    // The generation guard now also skips the in-flight callback's own busy/creatingVendor reset
    // for a discarded generation, so reset() must clear both itself rather than relying on that
    // callback to self-heal them.
    this.busy.set(false);
  }

  private tooLarge(): string { return 'That file is larger than the 15 MB limit.'; }
}
