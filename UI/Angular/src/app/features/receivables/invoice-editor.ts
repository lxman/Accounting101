import { ChangeDetectionStrategy, Component, computed, DestroyRef, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { form, applyEach, required, FormField } from '@angular/forms/signals';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { Invoice, InvoiceLine, DraftInvoiceRequest, invoiceTotals } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CurrencyInput } from '../../shared/currency-input';
import { CanDirective } from '../../core/capabilities/can.directive';

interface LineModel {
  lineId: string;
  description: string;
  quantity: number;
  unitPrice: number;
  taxable: boolean;
  revenueCategory: string | null;
}

interface InvoiceFormValue {
  customerId: string;
  issueDate: string;
  dueDate: string | null;
  memo: string | null;
  taxRate: number;
  lines: LineModel[];
}

const emptyLine = (): LineModel => ({
  lineId: crypto.randomUUID(),
  description: '',
  quantity: 0,
  unitPrice: 0,
  taxable: true,
  revenueCategory: null,
});

@Component({
  selector: 'app-invoice-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormField, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-4xl">
      <h1 class="text-2xl font-bold">{{ editId ? 'Edit invoice' : 'New invoice' }}</h1>

      <div class="flex flex-col gap-1">
        <label hlmLabel>Customer</label>
        <div hlmSelect [value]="form.customerId().value()" [itemToString]="customerLabel"
             (valueChange)="form.customerId().value.set($any($event))" [disabled]="!!editId">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a customer" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (c of svc.customers(); track c.id) {
              <hlm-select-item [value]="c.id">{{ c.name }}</hlm-select-item>
            }
          </hlm-select-content>
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Issue date</label>
          <input hlmInput type="date" [formField]="form.issueDate" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Due date</label>
          <input hlmInput type="date"
                 [value]="form.dueDate().value() ?? ''"
                 (change)="form.dueDate().value.set($any($event.target).value || null)" />
        </div>
      </div>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Tax rate</label>
          <input hlmInput type="number" step="0.01"
                 [value]="form.taxRate().value()"
                 (input)="form.taxRate().value.set(+$any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text"
                 [value]="form.memo().value() ?? ''"
                 (input)="form.memo().value.set($any($event.target).value || null)" />
        </div>
      </div>

      <table class="w-full text-sm">
        <thead>
          <tr class="text-left text-muted-foreground">
            <th class="py-1">Description</th>
            <th class="text-right pr-5">Qty</th>
            <th class="text-right pr-5">Unit price</th>
            <th class="text-center px-2">Taxable</th>
            <th class="pr-2">Category</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (line of model().lines; track line.lineId; let i = $index) {
            <tr>
              <td class="py-1 pr-2">
                <input hlmInput type="text" [formField]="form.lines[i].description" />
              </td>
              <td class="pr-2">
                <div class="flex justify-end">
                  <input hlmInput type="number" class="text-right tabular-nums w-20"
                         [value]="form.lines[i].quantity().value()"
                         (input)="form.lines[i].quantity().value.set(+$any($event.target).value)" />
                </div>
              </td>
              <td class="pr-2">
                <div class="flex justify-end">
                  <app-currency-input class="w-32" ariaLabel="Unit price"
                       [value]="form.lines[i].unitPrice().value()"
                       (valueChange)="form.lines[i].unitPrice().value.set($event)" />
                </div>
              </td>
              <td class="text-center">
                <input type="checkbox"
                       [checked]="form.lines[i].taxable().value()"
                       (change)="form.lines[i].taxable().value.set($any($event.target).checked)" />
              </td>
              <td class="pr-2">
                <input hlmInput type="text"
                       [value]="form.lines[i].revenueCategory().value() ?? ''"
                       (input)="form.lines[i].revenueCategory().value.set($any($event.target).value || null)" />
              </td>
              <td>
                <button hlmBtn type="button" variant="ghost" size="sm" (click)="removeLine(i)">✕</button>
              </td>
            </tr>
          }
        </tbody>
      </table>

      <button hlmBtn type="button" variant="outline" size="sm" (click)="addLine()">+ Add line</button>

      <div class="text-right text-sm tabular-nums flex flex-col gap-1 w-56 ms-auto">
        <div class="flex justify-between"><span>Subtotal</span><span>{{ money(totals().subtotal) }}</span></div>
        <div class="flex justify-between"><span>Tax</span><span>{{ money(totals().tax) }}</span></div>
        <div class="flex justify-between font-semibold border-t border-border pt-1">
          <span>Total</span><span>{{ money(totals().total) }}</span>
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'ar.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Save</button>
        <a hlmBtn variant="outline" routerLink="/receivables">Cancel</a>
      </div>
    </div>
  `,
})
export class InvoiceEditor {
  #loaded = false;
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly editId = this.route.snapshot.paramMap.get('id');
  private readonly prefillCustomer = this.route.snapshot.queryParamMap.get('customer');

  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly model = signal<InvoiceFormValue>({
    customerId: this.prefillCustomer ?? '',
    issueDate: new Date().toISOString().slice(0, 10),
    dueDate: null,
    memo: null,
    taxRate: 0,
    lines: [],
  });

  readonly form = form(this.model, (p) => {
    required(p.customerId);
    required(p.issueDate);
    applyEach(p.lines, (l) => {
      required(l.description);
    });
  });

  readonly totals = computed(() => invoiceTotals(this.model().lines, this.model().taxRate));
  readonly canSave = computed(() => this.form().valid() && this.model().lines.length > 0 && this.totals().total > 0);

  readonly customerLabel = (id: string): string => this.svc.customerName(id);

  constructor() {
    this.svc.load();
    if (this.editId) {
      effect(() => {
        if (this.#loaded) return;
        const customers = this.svc.customers();
        if (!customers.length) return;
        this.#loaded = true;
        this.svc.getInvoice(this.editId!).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(view => {
          this.model.set(this.fromInvoice(view.invoice));
        });
      });
    }
  }

  addLine(): void { this.model.update(v => ({ ...v, lines: [...v.lines, emptyLine()] })); }
  removeLine(i: number): void { this.model.update(v => ({ ...v, lines: v.lines.filter((_, idx) => idx !== i) })); }

  money(n: number): string { return fmtMoney(n); }

  private fromInvoice(inv: Invoice): InvoiceFormValue {
    return {
      customerId: inv.customerId,
      issueDate: inv.issueDate,
      dueDate: inv.dueDate,
      memo: inv.memo,
      taxRate: inv.taxRate,
      lines: inv.lines.map(l => ({ lineId: crypto.randomUUID(), ...l })),
    };
  }

  private toRequest(): DraftInvoiceRequest {
    const v = this.model();
    return {
      customerId: v.customerId,
      issueDate: v.issueDate,
      dueDate: v.dueDate,
      memo: v.memo,
      taxRate: v.taxRate,
      lines: v.lines.map(l => ({
        description: l.description,
        quantity: l.quantity,
        unitPrice: l.unitPrice,
        taxable: l.taxable,
        revenueCategory: l.revenueCategory,
      })),
    };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true);
    this.message.set(null);
    const req = this.toRequest();
    const obs = this.editId
      ? this.svc.updateDraft(this.editId, req)
      : this.svc.draft(req);
    obs.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => { this.busy.set(false); this.router.navigate(['/receivables/invoices', saved.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
