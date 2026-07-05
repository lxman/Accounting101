import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { AssetView } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { CurrencyInput } from '../../shared/currency-input';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-dispose-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CurrencyInput, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Dispose asset</h1>

      @if (asset(); as v) {
        <div class="text-sm text-muted-foreground border-b border-border pb-2">
          {{ v.asset.description }} · cost {{ money(v.asset.acquisitionCost) }} · net book value {{ money(v.netBookValue) }}
        </div>
      }

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Disposal date</label>
          <input hlmInput type="date" [value]="disposalDate()" (change)="disposalDate.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Proceeds</label>
          <app-currency-input ariaLabel="Proceeds" [value]="proceeds()" (valueChange)="proceeds.set($event)" />
          <span class="text-xs text-muted-foreground">Enter 0 for a retirement / scrap.</span>
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'fixedassets.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Dispose</button>
        <a hlmBtn variant="outline" [routerLink]="['/fixed-assets/assets', id]">Cancel</a>
      </div>
    </div>
  `,
})
export class DisposeEditor {
  private readonly svc = inject(FixedAssetsService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly asset = signal<AssetView | null>(null);
  readonly disposalDate = signal(new Date().toISOString().slice(0, 10));
  readonly proceeds = signal(0);
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly canSave = computed(() => !!this.disposalDate() && this.proceeds() >= 0);

  constructor() {
    this.svc.getAsset(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.asset.set(v),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.disposeAsset(this.id, { disposalDate: this.disposalDate(), proceeds: this.proceeds(), memo: this.memo() })
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (d) => { this.busy.set(false); void this.router.navigate(['/fixed-assets/disposals', d.id]); },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }

  money(n: number): string { return fmtMoney(n); }
}
