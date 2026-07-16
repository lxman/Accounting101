import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { InventoryService } from '../../core/inventory/inventory.service';
import { ItemView } from '../../core/inventory/inventory';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { Paginator } from '../../shared/paginator';
import { TruncateDirective } from '../../shared/truncate.directive';

@Component({
  selector: 'app-item-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports, Paginator, TruncateDirective],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Inventory items</h1>
        <a *appCan="'inventory.write'" hlmBtn size="sm" routerLink="/inventory/items/new" class="ms-auto">New item</a>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (items().length === 0) {
        <p class="text-muted-foreground text-sm">No items yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>SKU</th><th hlmTh>Name</th><th hlmTh class="text-right">On hand</th>
                <th hlmTh class="text-right">Avg cost</th><th hlmTh>Status</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (v of items(); track v.item.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(v.item.id)" (keydown.enter)="open(v.item.id)">
                  <td hlmTd>{{ v.item.sku }}</td>
                  <td hlmTd><span appTruncate>{{ v.item.name }}</span></td>
                  <td hlmTd class="text-right tabular-nums">{{ v.item.onHandQuantity }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(v.averageUnitCost) }}</td>
                  <td hlmTd>{{ v.item.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Items pagination" (previous)="prev()" (next)="next()" />
      }
    </div>
  `,
})
export class ItemList {
  private readonly svc = inject(InventoryService);
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
        return this.svc.listItems({ skip, limit }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading items'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<ItemView> | null },
  );

  readonly items = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  open(id: string): void { void this.router.navigate(['/inventory/items', id]); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
}
