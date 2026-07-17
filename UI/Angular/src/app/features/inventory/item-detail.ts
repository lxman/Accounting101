import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { InventoryService } from '../../core/inventory/inventory.service';
import { ItemView, StockMovement } from '../../core/inventory/inventory';
import { PagedResponse } from '../../core/api/paged-response';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { Paginator } from '../../shared/paginator';

@Component({
  selector: 'app-item-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports, Paginator],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/inventory" class="text-sm text-muted-foreground hover:text-foreground">← Items</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ v.item.sku }} · {{ v.item.name }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="v.item.status === 'Inactive'">{{ v.item.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Unit of measure</td><td class="text-right">{{ v.item.unitOfMeasure }}</td></tr>
            @if (v.item.description) { <tr><td class="py-1 text-muted-foreground">Description</td><td class="text-right">{{ v.item.description }}</td></tr> }
            <tr><td class="py-1 text-muted-foreground">On hand</td><td class="text-right tabular-nums">{{ v.item.onHandQuantity }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Average unit cost</td><td class="text-right tabular-nums">{{ money(v.averageUnitCost) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Total value</td><td class="text-right tabular-nums">{{ money(v.item.totalValue) }}</td></tr>
          </tbody>
        </table>

        <div *appCan="'inventory.write'" class="flex items-center gap-2 border-t border-border pt-4">
          <a hlmBtn [routerLink]="['/inventory/movements/new']" [queryParams]="{ itemId: v.item.id }">New movement</a>
          <a hlmBtn variant="outline" [routerLink]="['/inventory/items', v.item.id, 'edit']">Edit</a>
          @if (v.item.status === 'Active') {
            <button hlmBtn variant="outline" type="button" (click)="deactivate()" [disabled]="!canDeactivate() || busy()">Deactivate</button>
          } @else {
            <button hlmBtn variant="outline" type="button" (click)="reactivate()" [disabled]="busy()">Reactivate</button>
          }
        </div>

        <h2 class="text-lg font-semibold border-t border-border pt-4">Movement history</h2>
        @if (movements().length === 0) {
          <p class="text-muted-foreground text-sm">No movements yet.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>#</th><th hlmTh>Type</th><th hlmTh>Effective date</th>
                  <th hlmTh class="text-right">Quantity</th><th hlmTh class="text-right">Unit cost</th>
                  <th hlmTh>Status</th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (m of movements(); track m.id) {
                  <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                      (click)="openMovement(m.id)" (keydown.enter)="openMovement(m.id)">
                    <td hlmTd>{{ m.number ?? '—' }}</td>
                    <td hlmTd>{{ m.type }}</td>
                    <td hlmTd>{{ formatDate(m.effectiveDate) }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ m.quantity }}</td>
                    <td hlmTd class="text-right tabular-nums">{{ money(m.appliedUnitCost) }}</td>
                    <td hlmTd [class.text-destructive]="m.status === 'Void'">{{ m.status }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Movement history pagination" (previous)="prev()" (next)="next()" [pageSize]="limit()" (pageSizeChange)="setPageSize($event)" />
        }
      }
    </div>
  `,
})
export class ItemDetail {
  private readonly svc = inject(InventoryService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<ItemView | null>(null);
  readonly message = signal<string | null>(null);
  readonly busy = signal(false);

  readonly skip = signal(0);
  readonly limit = signal(20);

  private readonly query = computed(() => ({ skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      switchMap(({ skip, limit }) => this.svc.listMovements(this.id, { skip, limit }).pipe(
        catchError((e: unknown) => { this.message.set((e as { message?: string })?.message ?? 'Error loading movements'); return of(null); }),
      )),
    ),
    { initialValue: null as PagedResponse<StockMovement> | null },
  );

  readonly movements = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  readonly canDeactivate = computed(() => (this.view()?.item.onHandQuantity ?? 0) === 0);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getItem(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.view.set(v); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  deactivate(): void {
    if (!this.canDeactivate()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.deactivateItem(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  reactivate(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.reactivateItem(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.view.set(v); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  openMovement(id: string): void { void this.router.navigate(['/inventory/movements', id]); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  setPageSize(n: number): void { this.limit.set(n); this.skip.set(0); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
