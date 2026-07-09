import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { InventoryService } from '../../core/inventory/inventory.service';
import { MovementType, RecordMovementRequest } from '../../core/inventory/inventory';
import { extractProblem } from '../../core/api/problem-details';
import { CurrencyInput } from '../../shared/currency-input';
import { CanDirective } from '../../core/capabilities/can.directive';

type AdjustmentDirection = 'Overage' | 'Shrinkage';

@Component({
  selector: 'app-movement-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, ...HlmSelectImports, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">New movement</h1>

      @if (!itemId()) {
        <p class="text-destructive text-sm">No item selected. Open a movement from an item's detail page.</p>
      } @else {
        <div class="grid grid-cols-2 gap-4">
          <div class="flex flex-col gap-1">
            <label hlmLabel>Type</label>
            <div hlmSelect [value]="type()" (valueChange)="onTypeChange($any($event))" class="w-full">
              <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
              <hlm-select-content *hlmSelectPortal>
                <hlm-select-item value="Receipt">Receipt</hlm-select-item>
                <hlm-select-item value="Issue">Issue</hlm-select-item>
                <hlm-select-item value="Adjustment">Adjustment</hlm-select-item>
              </hlm-select-content>
            </div>
          </div>

          @if (type() === 'Adjustment') {
            <div class="flex flex-col gap-1">
              <label hlmLabel>Direction</label>
              <div hlmSelect [value]="direction()" (valueChange)="direction.set($any($event))" class="w-full">
                <hlm-select-trigger class="w-full"><hlm-select-value /></hlm-select-trigger>
                <hlm-select-content *hlmSelectPortal>
                  <hlm-select-item value="Overage">Overage</hlm-select-item>
                  <hlm-select-item value="Shrinkage">Shrinkage</hlm-select-item>
                </hlm-select-content>
              </div>
            </div>
          }

          <div class="flex flex-col gap-1">
            <label hlmLabel>Quantity</label>
            <input hlmInput type="number" min="0" step="1" [value]="quantityMagnitude()" (input)="quantityMagnitude.set(+$any($event.target).value)" />
          </div>

          @if (showUnitCost()) {
            <div class="flex flex-col gap-1">
              <label hlmLabel>Unit cost</label>
              <app-currency-input ariaLabel="Unit cost" [value]="unitCost()" (valueChange)="unitCost.set($event)" />
            </div>
          }

          <div class="flex flex-col gap-1">
            <label hlmLabel>Effective date</label>
            <input hlmInput type="date" [value]="effectiveDate()" (change)="effectiveDate.set($any($event.target).value)" />
          </div>

          <div class="flex flex-col gap-1 col-span-2">
            <label hlmLabel>Memo</label>
            <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
          </div>
        </div>

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

        <div class="flex items-center gap-2">
          <button *appCan="'inventory.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Record movement</button>
          <a hlmBtn variant="outline" [routerLink]="itemId() ? ['/inventory/items', itemId()] : ['/inventory']">Cancel</a>
        </div>
      }
    </div>
  `,
})
export class MovementEditor {
  private readonly svc = inject(InventoryService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly itemId = signal<string | null>(this.route.snapshot.queryParamMap.get('itemId'));
  readonly type = signal<MovementType>('Receipt');
  readonly direction = signal<AdjustmentDirection>('Overage');
  readonly quantityMagnitude = signal(0);
  readonly unitCost = signal(0);
  readonly effectiveDate = signal(new Date().toISOString().slice(0, 10));
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  /** Unit-cost is meaningful for a Receipt (stock comes in at a cost) and for a positive
   *  Adjustment / overage (found stock is valued in); it's hidden for an Issue (costed by the
   *  engine's weighted-average) and for a negative Adjustment / shrinkage (no cost to apply). */
  readonly showUnitCost = computed(() => {
    if (this.type() === 'Receipt') return true;
    if (this.type() === 'Issue') return false;
    return this.direction() === 'Overage';
  });

  readonly signedQuantity = computed(() => {
    const mag = Math.abs(this.quantityMagnitude());
    // Receipt and Issue are positive magnitudes; the backend derives an Issue's
    // stock-decreasing effect from the Type. Only an Adjustment carries a sign.
    if (this.type() === 'Adjustment') return this.direction() === 'Shrinkage' ? -mag : mag;
    return mag;
  });

  readonly canSave = computed(() =>
    !!this.itemId() && this.quantityMagnitude() > 0 && !!this.effectiveDate() &&
    (!this.showUnitCost() || this.unitCost() > 0));

  onTypeChange(t: MovementType): void { this.type.set(t); }

  private body(): RecordMovementRequest {
    return {
      itemId: this.itemId()!,
      type: this.type(),
      quantity: this.signedQuantity(),
      unitCost: this.showUnitCost() ? this.unitCost() : null,
      effectiveDate: this.effectiveDate(),
      memo: this.memo(),
    };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.recordMovement(this.body()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (m) => { this.busy.set(false); void this.router.navigate(['/inventory/movements', m.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
