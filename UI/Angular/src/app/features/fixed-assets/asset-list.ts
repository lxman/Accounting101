import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { AssetView, methodLabel } from '../../core/fixed-assets/fixed-assets';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { Paginator } from '../../shared/paginator';
import { TruncateDirective } from '../../shared/truncate.directive';

@Component({
  selector: 'app-asset-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports, Paginator, TruncateDirective],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Asset register</h1>
        <a *appCan="'fixedassets.write'" hlmBtn size="sm" routerLink="/fixed-assets/assets/new" class="ms-auto">New asset</a>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (assets().length === 0) {
        <p class="text-muted-foreground text-sm">No assets yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>Description</th><th hlmTh class="text-right">Cost</th>
                <th hlmTh class="text-right">Net book value</th><th hlmTh>Method</th><th hlmTh>Status</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (v of assets(); track v.asset.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(v.asset.id)" (keydown.enter)="open(v.asset.id)">
                  <td hlmTd><span appTruncate>{{ v.asset.description }}</span></td>
                  <td hlmTd class="text-right tabular-nums">{{ money(v.asset.acquisitionCost) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(v.netBookValue) }}</td>
                  <td hlmTd>{{ method(v.asset.method) }}</td>
                  <td hlmTd>{{ v.asset.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Assets pagination" (previous)="prev()" (next)="next()" />
      }
    </div>
  `,
})
export class AssetList {
  private readonly svc = inject(FixedAssetsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listAssets({ skip, limit }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading assets'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<AssetView> | null },
  );

  readonly assets = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  open(id: string): void { void this.router.navigate(['/fixed-assets/assets', id]); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
  method(m: AssetView['asset']['method']): string { return methodLabel(m); }
}
