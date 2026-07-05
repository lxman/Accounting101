import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { DepreciationMethod, SaveAssetRequest } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { CurrencyInput } from '../../shared/currency-input';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-asset-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">{{ editId() ? 'Edit asset' : 'New asset' }}</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Description</label>
          <input hlmInput type="text" [value]="description()" (input)="description.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Acquisition cost</label>
          <app-currency-input ariaLabel="Acquisition cost" [value]="acquisitionCost()" (valueChange)="acquisitionCost.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>In-service date</label>
          <input hlmInput type="date" [value]="inServiceDate()" (change)="inServiceDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Useful life (months)</label>
          <input hlmInput type="number" min="1" [value]="usefulLifeMonths()" (input)="usefulLifeMonths.set(+$any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Salvage value</label>
          <app-currency-input ariaLabel="Salvage value" [value]="salvageValue()" (valueChange)="salvageValue.set($event)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Method</label>
          <select hlmInput [value]="method()" (change)="method.set($any($event.target).value)">
            <option value="StraightLine">Straight line</option>
            <option value="DecliningBalance">Declining balance</option>
          </select>
        </div>
        @if (showFactor()) {
          <div class="flex flex-col gap-1">
            <label hlmLabel>Declining-balance factor</label>
            <input hlmInput type="number" min="0" step="0.1" [value]="factor() ?? ''" (input)="factor.set($any($event.target).value === '' ? null : +$any($event.target).value)" />
          </div>
        }
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'fixedassets.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">
          {{ editId() ? 'Save' : 'Create asset' }}
        </button>
        <a hlmBtn variant="outline" routerLink="/fixed-assets/assets">Cancel</a>
      </div>
    </div>
  `,
})
export class AssetEditor {
  private readonly svc = inject(FixedAssetsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly editId = signal<string | null>(this.route.snapshot.paramMap.get('id'));
  readonly description = signal('');
  readonly acquisitionCost = signal(0);
  readonly inServiceDate = signal(new Date().toISOString().slice(0, 10));
  readonly usefulLifeMonths = signal(12);
  readonly salvageValue = signal(0);
  readonly method = signal<DepreciationMethod>('StraightLine');
  readonly factor = signal<number | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly showFactor = computed(() => this.method() === 'DecliningBalance');
  readonly canSave = computed(() =>
    this.description().trim().length > 0 && this.acquisitionCost() > 0 && !!this.inServiceDate() &&
    this.usefulLifeMonths() > 0 && this.salvageValue() >= 0 &&
    (this.method() !== 'DecliningBalance' || (this.factor() ?? 0) > 0));

  constructor() {
    const id = this.editId();
    if (id) {
      this.svc.getAsset(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (v) => {
          this.description.set(v.asset.description); this.acquisitionCost.set(v.asset.acquisitionCost);
          this.inServiceDate.set(v.asset.inServiceDate); this.usefulLifeMonths.set(v.asset.usefulLifeMonths);
          this.salvageValue.set(v.asset.salvageValue); this.method.set(v.asset.method); this.factor.set(v.asset.decliningBalanceFactor);
        },
        error: (e) => this.message.set(extractProblem(e).detail),
      });
    }
  }

  private body(): SaveAssetRequest {
    return {
      description: this.description().trim(), acquisitionCost: this.acquisitionCost(), inServiceDate: this.inServiceDate(),
      usefulLifeMonths: this.usefulLifeMonths(), salvageValue: this.salvageValue(), method: this.method(),
      decliningBalanceFactor: this.method() === 'DecliningBalance' ? this.factor() : null,
    };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    const id = this.editId();
    const call = id ? this.svc.updateAsset(id, this.body()) : this.svc.createAsset(this.body());
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.busy.set(false); void this.router.navigate(['/fixed-assets/assets', v.asset.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
