import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { Disposal } from '../../core/fixed-assets/fixed-assets';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { Paginator } from '../../shared/paginator';
import { PaginationPrefsService } from '../../core/pagination/pagination-prefs.service';

@Component({
  selector: 'app-disposal-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  imports: [...HlmTableImports, Paginator],
  template: `
    <div class="flex flex-col gap-4 p-4 flex-1 min-h-0">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Disposals</h1>
        <label class="flex items-center gap-2 text-sm text-muted-foreground ms-auto">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" /> Show voided
        </label>
      </div>
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (disposals().length === 0) {
        <p class="text-muted-foreground text-sm">No disposals yet. Dispose an asset from its detail page.</p>
      } @else {
        <div hlmTableContainer class="flex-1 min-h-0 overflow-y-auto">
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>#</th><th hlmTh>Asset</th><th hlmTh>Date</th><th hlmTh class="text-right">Proceeds</th><th hlmTh class="text-right">Gain / loss</th><th hlmTh>Status</th></tr></thead>
            <tbody hlmTBody>
              @for (d of disposals(); track d.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0" (click)="open(d.id)" (keydown.enter)="open(d.id)">
                  <td hlmTd>{{ d.number ?? '—' }}</td>
                  <td hlmTd>{{ shortId(d.assetId) }}</td>
                  <td hlmTd>{{ formatDate(d.disposalDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(d.proceeds) }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-emerald-600]="d.gainLoss > 0" [class.text-destructive]="d.gainLoss < 0">{{ money(d.gainLoss) }}</td>
                  <td hlmTd>{{ d.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Disposals pagination" (previous)="prev()" (next)="next()" [pageSize]="limit()" (pageSizeChange)="setPageSize($event)" />
      }
    </div>
  `,
})
export class DisposalList {
  private readonly svc = inject(FixedAssetsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);
  private readonly prefs = inject(PaginationPrefsService);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = this.prefs.pageSize;
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, includeVoided, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listDisposals({ skip, limit, includeVoided }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading disposals'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<Disposal> | null },
  );

  readonly disposals = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  shortId(id: string): string { return id.slice(0, 8); }
  open(id: string): void { void this.router.navigate(['/fixed-assets/disposals', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  setPageSize(n: number): void { this.prefs.setPageSize(n); this.skip.set(0); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
