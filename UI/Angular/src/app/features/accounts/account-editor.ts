import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { form, required, FormField } from '@angular/forms/signals';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { AccountsService } from '../../core/accounts/accounts.service';
import { AccountResponse, AccountType } from '../../core/accounts/account';
import { isDescendant } from '../../core/accounts/account-tree';
import { extractProblem } from '../../core/api/problem-details';

interface EditorValue {
  number: string; name: string; type: AccountType; parentId: string | null;
  cashFlowActivity: string; postable: boolean; isRetainedEarnings: boolean; active: boolean;
}
const TYPES: AccountType[] = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense'];
const DEBIT_TYPES = new Set<AccountType>(['Asset', 'Expense']);

@Component({
  selector: 'app-account-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FormField, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-xl">
      <h1 class="text-2xl font-bold">{{ editId ? 'Edit account' : 'New account' }}</h1>

      <div class="flex flex-col gap-1">
        <label hlmLabel>Number</label>
        <input hlmInput type="text" [formField]="accountForm.number" />
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Name</label>
        <input hlmInput type="text" [formField]="accountForm.name" />
      </div>
      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Type</label>
          <div hlmSelect [value]="accountForm.type().value()" (valueChange)="accountForm.type().value.set($any($event))">
            <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
            <hlm-select-content *hlmSelectPortal>
              @for (t of types; track t) { <hlm-select-item [value]="t">{{ t }}</hlm-select-item> }
            </hlm-select-content>
          </div>
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Normal side</label>
          <input hlmInput type="text" [value]="normalSide()" readonly disabled />
        </div>
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Parent (same type)</label>
        <div hlmSelect [value]="accountForm.parentId().value() ?? ''" [itemToString]="parentLabel" (valueChange)="setParent($any($event))">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="— none (root) —" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">— none (root) —</hlm-select-item>
            @for (p of parentOptions(); track p.id) { <hlm-select-item [value]="p.id">{{ p.number }} {{ p.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>
      <div class="flex flex-col gap-1">
        <label hlmLabel>Cash-flow activity</label>
        <div hlmSelect [value]="accountForm.cashFlowActivity().value()" (valueChange)="accountForm.cashFlowActivity().value.set($any($event))">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="— none —" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">— none —</hlm-select-item>
            <hlm-select-item value="Operating">Operating</hlm-select-item>
            <hlm-select-item value="Investing">Investing</hlm-select-item>
            <hlm-select-item value="Financing">Financing</hlm-select-item>
          </hlm-select-content>
        </div>
      </div>
      <div class="flex flex-col gap-2 text-sm">
        <label class="flex items-center gap-2"><input type="checkbox" [checked]="accountForm.postable().value()" (change)="accountForm.postable().value.set($any($event.target).checked)" /> Postable (leaf account)</label>
        <label class="flex items-center gap-2"><input type="checkbox" [checked]="accountForm.isRetainedEarnings().value()" (change)="accountForm.isRetainedEarnings().value.set($any($event.target).checked)" /> Retained-earnings account</label>
        <label class="flex items-center gap-2"><input type="checkbox" [checked]="accountForm.active().value()" (change)="accountForm.active().value.set($any($event.target).checked)" /> Active</label>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
      <div class="flex items-center gap-2">
        <button hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Save</button>
        <a hlmBtn variant="outline" routerLink="/accounts">Cancel</a>
      </div>
    </div>
  `,
})
export class AccountEditor {
  #loaded = false;
  private readonly accounts = inject(AccountsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly types = TYPES;
  readonly editId = this.route.snapshot.paramMap.get('id'); // null on /accounts/new
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly model = signal<EditorValue>(this.initialValue());
  readonly accountForm = form(this.model, (p) => { required(p.number); required(p.name); required(p.type); });

  readonly canSave = computed(() => this.accountForm().valid());
  readonly normalSide = computed(() => (DEBIT_TYPES.has(this.model().type) ? 'Debit' : 'Credit'));

  // Same-type accounts, excluding self and the edited account's own descendants (no cycles).
  readonly parentOptions = computed<AccountResponse[]>(() => {
    const all = this.accounts.accounts();
    const type = this.model().type;
    return all.filter(a => a.type === type && a.id !== this.editId
      && !(this.editId ? isDescendant(all, this.editId, a.id) : false));
  });

  constructor() {
    if (this.accounts.accounts().length === 0) this.accounts.load();
    if (this.editId) {
      effect(() => {
        if (this.#loaded) return;
        const existing = this.accounts.byId().get(this.editId!);
        if (!existing) return;
        this.#loaded = true;
        this.model.set(this.fromAccount(existing));
      });
    }
  }

  readonly parentLabel = (id: string): string => {
    if (!id) return '— none (root) —';
    const a = this.accounts.byId().get(id); return a ? `${a.number} ${a.name}` : id;
  };
  setParent(v: string): void { this.accountForm.parentId().value.set(v === '' ? null : v); }

  private initialValue(): EditorValue {
    return { number: '', name: '', type: 'Asset', parentId: null, cashFlowActivity: '', postable: true, isRetainedEarnings: false, active: true };
  }
  private fromAccount(a: AccountResponse): EditorValue {
    return { number: a.number, name: a.name, type: a.type, parentId: a.parentId, cashFlowActivity: a.cashFlowActivity ?? '', postable: a.postable, isRetainedEarnings: a.isRetainedEarnings, active: a.active };
  }

  save(): void {
    if (!this.canSave()) return;
    const v = this.model();
    this.busy.set(true); this.message.set(null);
    this.accounts.upsert({
      id: this.editId ?? this.accounts.newId(), number: v.number, name: v.name, type: v.type, parentId: v.parentId,
      postable: v.postable, requiredDimension: null, cashFlowActivity: v.cashFlowActivity || null,
      isRetainedEarnings: v.isRetainedEarnings, active: v.active,
    }).subscribe({
      next: () => { this.busy.set(false); this.router.navigate(['/accounts']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
