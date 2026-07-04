import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { AssetView, methodLabel } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-asset-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/fixed-assets/assets" class="text-sm text-muted-foreground hover:text-foreground">← Assets</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ v.asset.description }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="v.asset.status === 'Disposed'">{{ v.asset.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Acquisition cost</td><td class="text-right tabular-nums">{{ money(v.asset.acquisitionCost) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">In-service date</td><td class="text-right">{{ formatDate(v.asset.inServiceDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Useful life</td><td class="text-right">{{ v.asset.usefulLifeMonths }} months</td></tr>
            <tr><td class="py-1 text-muted-foreground">Salvage value</td><td class="text-right tabular-nums">{{ money(v.asset.salvageValue) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Method</td><td class="text-right">{{ method(v.asset.method) }}</td></tr>
            @if (v.asset.method === 'DecliningBalance') { <tr><td class="py-1 text-muted-foreground">DB factor</td><td class="text-right">{{ v.asset.decliningBalanceFactor }}</td></tr> }
            <tr><td class="py-1 text-muted-foreground">Accumulated depreciation</td><td class="text-right tabular-nums">{{ money(v.asset.accumulatedDepreciation) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Net book value</td><td class="text-right tabular-nums">{{ money(v.netBookValue) }}</td></tr>
          </tbody>
        </table>

        @if (v.asset.status === 'Active') {
          <div *appCan="'fixedassets.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <a hlmBtn variant="outline" [routerLink]="['/fixed-assets/assets', v.asset.id, 'edit']">Edit</a>
            <a hlmBtn [routerLink]="['/fixed-assets/assets', v.asset.id, 'dispose']">Dispose asset</a>
          </div>
        } @else {
          <a routerLink="/fixed-assets/disposals" class="text-sm text-primary hover:underline">View disposals →</a>
        }
      }
    </div>
  `,
})
export class AssetDetail {
  private readonly svc = inject(FixedAssetsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<AssetView | null>(null);
  readonly message = signal<string | null>(null);

  constructor() {
    this.svc.getAsset(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  method(m: AssetView['asset']['method']): string { return methodLabel(m); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
