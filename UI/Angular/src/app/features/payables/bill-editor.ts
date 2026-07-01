import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { form, applyEach, required, FormField } from '@angular/forms/signals';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { PayablesService } from '../../core/payables/payables.service';
import { Bill, DraftBillRequest, billTotal } from '../../core/payables/payables';
import { AccountsService } from '../../core/accounts/accounts.service';
import { AccountResponse } from '../../core/accounts/account';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';

interface LineModel { lineId: string; description: string; amount: number; expenseAccountId: string | null; }
interface BillFormValue {
  vendorId: string; billDate: string; dueDate: string | null;
  vendorReference: string | null; memo: string | null; lines: LineModel[];
}

const emptyLine = (): LineModel => ({ lineId: crypto.randomUUID(), description: '', amount: 0, expenseAccountId: null });

@Component({
  selector: 'app-bill-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormField, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports, CurrencyInput],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-4xl">
      <h1 class="text-2xl font-bold">{{ editId ? 'Edit bill' : 'New bill' }}</h1>

      <div class="flex flex-col gap-1">
        <label hlmLabel>Vendor</label>
        <div hlmSelect [value]="form.vendorId().value()" [itemToString]="vendorLabel"
             (valueChange)="form.vendorId().value.set($any($event))" [disabled]="!!editId">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a vendor" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (v of svc.vendors(); track v.id) { <hlm-select-item [value]="v.id">{{ v.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1"><label hlmLabel>Bill date</label>
          <input hlmInput type="date" [formField]="form.billDate" /></div>
        <div class="flex flex-col gap-1"><label hlmLabel>Due date</label>
          <input hlmInput type="date" [value]="form.dueDate().value() ?? ''"
                 (change)="form.dueDate().value.set($any($event.target).value || null)" /></div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1"><label hlmLabel>Vendor reference</label>
          <input hlmInput type="text" [value]="form.vendorReference().value() ?? ''"
                 (input)="form.vendorReference().value.set($any($event.target).value || null)" /></div>
        <div class="flex flex-col gap-1"><label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="form.memo().value() ?? ''"
                 (input)="form.memo().value.set($any($event.target).value || null)" /></div>
      </div>

      <table class="w-full text-sm">
        <thead><tr class="text-left text-muted-foreground">
          <th class="py-1">Description</th><th class="pr-2">Expense account</th>
          <th class="text-right pr-5">Amount</th><th></th>
        </tr></thead>
        <tbody>
          @for (line of model().lines; track line.lineId; let i = $index) {
            <tr>
              <td class="py-1 pr-2"><input hlmInput type="text" [formField]="form.lines[i].description" /></td>
              <td class="pr-2">
                <div hlmSelect [value]="form.lines[i].expenseAccountId().value() ?? ''" [itemToString]="accountLabel"
                     (valueChange)="form.lines[i].expenseAccountId().value.set($any($event) || null)">
                  <hlm-select-trigger class="w-56"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
                  <hlm-select-content *hlmSelectPortal>
                    @for (a of expenseAccounts(); track a.id) {
                      <hlm-select-item [value]="a.id">{{ a.number }} · {{ a.name }}</hlm-select-item>
                    }
                  </hlm-select-content>
                </div>
              </td>
              <td class="pr-2"><div class="flex justify-end">
                <app-currency-input class="w-32" ariaLabel="Amount"
                     [value]="form.lines[i].amount().value()"
                     (valueChange)="form.lines[i].amount().value.set($event)" /></div></td>
              <td><button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)">✕</button></td>
            </tr>
          }
        </tbody>
      </table>

      <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>

      <div class="text-right text-sm tabular-nums flex flex-col gap-1 w-56 ms-auto">
        <div class="flex justify-between font-semibold border-t border-border pt-1">
          <span>Total</span><span>{{ money(total()) }}</span></div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Save</button>
        <a hlmBtn variant="outline" routerLink="/payables">Cancel</a>
        @if (editId) {
          <button hlmBtn type="button" variant="ghost" (click)="discard()" [disabled]="busy()">Discard</button>
        }
      </div>
    </div>
  `,
})
export class BillEditor {
  #loaded = false;
  readonly svc = inject(PayablesService);
  readonly accountsSvc = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly editId = this.route.snapshot.paramMap.get('id');

  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly model = signal<BillFormValue>({
    vendorId: this.svc.selectedVendorId() ?? '',
    billDate: new Date().toISOString().slice(0, 10),
    dueDate: null, vendorReference: null, memo: null, lines: [],
  });

  readonly form = form(this.model, (p) => {
    required(p.vendorId);
    required(p.billDate);
    applyEach(p.lines, (l) => { required(l.description); required(l.expenseAccountId); });
  });

  readonly expenseAccounts = computed<AccountResponse[]>(() =>
    this.accountsSvc.accounts().filter(a => a.type === 'Expense' && a.postable && a.active));

  readonly total = computed(() => billTotal(this.model().lines.map(l => ({ amount: l.amount }))));
  readonly canSave = computed(() =>
    this.form().valid() &&
    this.model().lines.length > 0 &&
    this.model().lines.every(l => l.amount > 0 && !!l.expenseAccountId) &&
    this.total() > 0);

  readonly vendorLabel = (id: string): string => this.svc.vendorName(id);
  readonly accountLabel = (id: string): string => {
    const a = this.accountsSvc.accounts().find(x => x.id === id);
    return a ? `${a.number} · ${a.name}` : id;
  };

  constructor() {
    this.svc.load();
    this.accountsSvc.load();
    if (this.editId) {
      effect(() => {
        if (this.#loaded) return;
        const vendors = this.svc.vendors();
        if (!vendors.length) return;
        this.#loaded = true;
        this.svc.getBill(this.editId!).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(view => {
          this.model.set(this.fromBill(view.bill));
        });
      });
    }
  }

  addLine(): void { this.model.update(v => ({ ...v, lines: [...v.lines, emptyLine()] })); }
  removeLine(i: number): void { this.model.update(v => ({ ...v, lines: v.lines.filter((_, idx) => idx !== i) })); }
  money(n: number): string { return fmtMoney(n); }

  private fromBill(b: Bill): BillFormValue {
    return {
      vendorId: b.vendorId,
      billDate: b.billDate,
      dueDate: b.dueDate,
      vendorReference: b.vendorReference,
      memo: b.memo,
      lines: (b.lines ?? []).map(l => ({ lineId: crypto.randomUUID(), description: l.description, amount: l.amount, expenseAccountId: l.expenseAccountId })),
    };
  }

  private toRequest(): DraftBillRequest {
    const v = this.model();
    return {
      vendorId: v.vendorId, billDate: v.billDate, dueDate: v.dueDate,
      vendorReference: v.vendorReference, memo: v.memo,
      lines: v.lines.map(l => ({ description: l.description, amount: l.amount, expenseAccountId: l.expenseAccountId! })),
    };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true);
    this.message.set(null);
    const req = this.toRequest();
    const obs = this.editId ? this.svc.editBill(this.editId, req) : this.svc.draftBill(req);
    obs.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => { this.busy.set(false); void this.router.navigate(['/payables/bills', saved.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  discard(): void {
    if (!this.editId) return;
    this.busy.set(true); this.message.set(null);
    this.svc.discardBill(this.editId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/payables']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
