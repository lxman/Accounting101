import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { PostingAccountsService } from '../../core/posting-accounts/posting-accounts.service';
import { PostingAccountSlot, ChartAccount } from '../../core/posting-accounts/posting-accounts';

const MODULE_LABELS: Record<string, string> = {
  cash: 'Cash & Banking', receivables: 'Receivables', payables: 'Payables',
  payroll: 'Payroll', fixedassets: 'Fixed Assets', inventory: 'Inventory',
};

interface ModuleGroup { moduleKey: string; label: string; slots: PostingAccountSlot[]; }

@Component({
  selector: 'app-posting-accounts',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective],
  template: `
    <h1 class="text-xl font-semibold mb-4">Posting accounts</h1>

    @if (error()) {
      <p class="text-red-600 mb-2">{{ error() }}</p>
    } @else {
      <p class="text-sm text-muted-foreground mb-4">The chart accounts each module posts to for this client.
         Leaving a slot unset uses the deployment default.</p>

      @if (!ready()) {
        <p class="text-sm text-muted-foreground">Loading…</p>
      } @else {
        @if (groups().length === 0) {
          <p class="text-sm text-muted-foreground">No modules with posting accounts are enabled for this client.</p>
        }

        @for (g of groups(); track g.moduleKey) {
          <section class="mb-6">
            <h2 class="font-medium mb-2">{{ g.label }}</h2>
            <div class="space-y-3">
              @for (s of g.slots; track s.slotKey) {
                <label class="block">
                  <span class="text-sm font-medium">{{ s.label }}</span>
                  <span class="ms-2 text-xs text-muted-foreground">expects {{ s.expectedType }}</span>
                  <select class="mt-1 block w-96 rounded border border-border bg-background px-3 py-2 text-sm"
                          [value]="chosen()[key(g.moduleKey, s.slotKey)] ?? ''"
                          (change)="onSelect(g.moduleKey, s.slotKey, $event)"
                          [attr.data-testid]="'slot-' + g.moduleKey + '-' + s.slotKey">
                    <option value="" [selected]="(chosen()[key(g.moduleKey, s.slotKey)] ?? '') === ''">— deployment default —</option>
                    @for (a of postableAccounts(); track a.id) {
                      <option [value]="a.id" [selected]="a.id === (chosen()[key(g.moduleKey, s.slotKey)] ?? '')">{{ a.number }} · {{ a.name }} ({{ a.type }})</option>
                    }
                  </select>
                </label>
              }
              @if (savedModule() === g.moduleKey) { <p class="text-green-600 text-sm">Saved.</p> }
              <button *appCan="'admin.postingAccounts'" hlmBtn (click)="save(g.moduleKey)">Save {{ g.label }}</button>
            </div>
          </section>
        }
      }
    }
  `,
})
export class PostingAccountsScreen {
  private readonly service = inject(PostingAccountsService);

  readonly slots = signal<PostingAccountSlot[]>([]);
  readonly postableAccounts = signal<ChartAccount[]>([]);
  readonly error = signal<string | null>(null);
  readonly savedModule = signal<string | null>(null);
  // Both GETs land in separate change-detection cycles on the live stack; gate the render
  // until BOTH resolve so each <select> first paints with its options present and [value] sticks.
  readonly slotsLoaded = signal(false);
  readonly accountsLoaded = signal(false);
  readonly ready = computed(() => this.slotsLoaded() && this.accountsLoaded());
  // slot composite key -> chosen account id ('' = default)
  private readonly chosenMap = signal<Record<string, string>>({});
  readonly chosen = this.chosenMap.asReadonly();

  readonly groups = computed<ModuleGroup[]>(() => {
    const byModule = new Map<string, PostingAccountSlot[]>();
    for (const s of this.slots()) {
      const list = byModule.get(s.moduleKey) ?? [];
      list.push(s);
      byModule.set(s.moduleKey, list);
    }
    return [...byModule.entries()].map(([moduleKey, slots]) => ({
      moduleKey, label: MODULE_LABELS[moduleKey] ?? moduleKey, slots,
    }));
  });

  constructor() {
    this.service.get().subscribe({
      next: (p) => {
        this.slots.set(p.slots);
        this.chosenMap.set(Object.fromEntries(
          p.slots.map((s) => [this.key(s.moduleKey, s.slotKey), s.currentAccountId ?? ''])));
        this.slotsLoaded.set(true);
      },
      error: () => this.error.set('Could not load posting accounts.'),
    });
    this.service.accounts().subscribe({
      next: (a) => { this.postableAccounts.set(a.filter((x) => x.postable)); this.accountsLoaded.set(true); },
      error: () => this.error.set('Could not load the chart of accounts.'),
    });
  }

  key(moduleKey: string, slotKey: string): string { return `${moduleKey}:${slotKey}`; }

  onSelect(moduleKey: string, slotKey: string, event: Event): void {
    this.selectAccount(moduleKey, slotKey, (event.target as HTMLSelectElement).value);
  }

  selectAccount(moduleKey: string, slotKey: string, accountId: string): void {
    this.chosenMap.update((m) => ({ ...m, [this.key(moduleKey, slotKey)]: accountId }));
    this.savedModule.set(null);
  }

  save(moduleKey: string): void {
    this.error.set(null);
    const slots: Record<string, string> = {};
    for (const s of this.slots().filter((x) => x.moduleKey === moduleKey)) {
      const chosen = this.chosenMap()[this.key(moduleKey, s.slotKey)];
      if (chosen) slots[s.slotKey] = chosen;   // omit unset slots (deployment default)
    }
    this.service.setModule(moduleKey, slots).subscribe({
      next: () => this.savedModule.set(moduleKey),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }
}
