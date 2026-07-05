import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal, effect } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankAdjustment, AdjustmentKind, RecordAdjustmentRequest, adjustmentKindLabel } from '../../core/banking/banking';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-adjustments-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports],
  template: `
    <div class="flex flex-col gap-3 border-t border-border pt-4">
      <h2 class="font-semibold">Bank adjustments</h2>

      @if (adjustments().length === 0) {
        <p class="text-sm text-muted-foreground">No adjustments recorded.</p>
      } @else {
        <table class="w-full text-sm">
          <thead><tr class="text-left text-muted-foreground"><th class="py-1">Number</th><th>Type</th>
            <th>Account</th><th class="text-right">Amount</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (a of adjustments(); track a.id) {
              <tr>
                <td class="py-1">{{ a.number ?? '—' }}</td>
                <td>{{ kindLabel(a.kind) }}</td>
                <td>{{ label(a.offsetAccountId) }}</td>
                <td class="text-right tabular-nums">{{ money(a.amount) }}</td>
                <td [class.text-destructive]="a.status === 'Void'">{{ a.status }}</td>
                <td class="text-right">
                  @if (a.status === 'Posted' && !locked()) {
                    <button *appCan="'bankrec.write'" hlmBtn size="sm" variant="ghost" (click)="voidAdjustment(a.id)" [disabled]="busy()">Void</button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      }

      @if (!locked()) {
        <div *appCan="'bankrec.write'" class="grid grid-cols-3 gap-3 items-end border border-border rounded p-3">
          <div class="flex flex-col gap-1">
            <label hlmLabel>Type</label>
            <div hlmSelect [value]="kind()" (valueChange)="kind.set($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                <hlm-select-item value="Charge">Bank charge</hlm-select-item>
                <hlm-select-item value="Credit">Bank interest</hlm-select-item>
              </hlm-select-content>
            </div>
          </div>
          <div class="flex flex-col gap-1">
            <label hlmLabel>Offset account</label>
            <div hlmSelect [value]="offsetAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="offsetAccountId.set($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select account" /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                @for (ac of postableAccounts(); track ac.id) { <hlm-select-item [value]="ac.id">{{ ac.number }} {{ ac.name }}</hlm-select-item> }
              </hlm-select-content>
            </div>
          </div>
          <div class="flex flex-col gap-1">
            <label hlmLabel>Amount</label>
            <input hlmInput type="number" class="text-right tabular-nums" [value]="amount() ?? ''" (input)="amount.set($any($event.target).value === '' ? null : +$any($event.target).value)" />
          </div>
          <div class="flex flex-col gap-1">
            <label hlmLabel>Date</label>
            <input hlmInput type="date" [value]="date() ?? ''" (input)="date.set($any($event.target).value === '' ? null : $any($event.target).value)" />
          </div>
          <div class="flex flex-col gap-1 col-span-2">
            <label hlmLabel>Memo</label>
            <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value === '' ? null : $any($event.target).value)" />
          </div>
          <button hlmBtn type="button" (click)="record()" [disabled]="!canRecord() || busy()">Record</button>
        </div>
      }

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
    </div>
  `,
})
export class AdjustmentsPanel {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly reconciliationId = input.required<string>();
  readonly locked = input(false);
  readonly changed = output<void>();

  readonly adjustments = signal<BankAdjustment[]>([]);
  readonly kind = signal<AdjustmentKind>('Charge');
  readonly offsetAccountId = signal<string | null>(null);
  readonly amount = signal<number | null>(null);
  readonly memo = signal<string | null>(null);
  readonly date = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly postableAccounts = computed(() => this.accounts.accounts().filter(a => a.postable));
  readonly canRecord = computed(() => !!this.offsetAccountId() && (this.amount() ?? 0) > 0);

  constructor() {
    if (this.accounts.accounts().length === 0) this.accounts.load();
    // Reload the list whenever the bound reconciliation id changes.
    effect(() => { const id = this.reconciliationId(); if (id) this.reload(id); });
  }

  private reload(id: string): void {
    this.svc.listAdjustments(id, { skip: 0, limit: 50 }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (p) => this.adjustments.set(p.items),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  readonly accountItemToString = (id: string): string => this.accounts.label(id);

  record(): void {
    if (!this.canRecord()) return;
    this.busy.set(true); this.message.set(null);
    const req: RecordAdjustmentRequest = { offsetAccountId: this.offsetAccountId()!, amount: this.amount()!, kind: this.kind(), date: this.date(), memo: this.memo() };
    this.svc.recordAdjustment(this.reconciliationId(), req).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.amount.set(null); this.offsetAccountId.set(null); this.memo.set(null); this.date.set(null); this.busy.set(false); this.reload(this.reconciliationId()); this.changed.emit(); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  voidAdjustment(adjId: string): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidAdjustment(this.reconciliationId(), adjId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.reload(this.reconciliationId()); this.changed.emit(); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  kindLabel(k: AdjustmentKind): string { return adjustmentKindLabel(k); }
  label(id: string): string { return this.accounts.label(id); }
  money(n: number): string { return fmtMoney(n); }
}
