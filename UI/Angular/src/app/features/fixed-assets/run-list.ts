import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { DepreciationRun } from '../../core/fixed-assets/fixed-assets';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-fa-run-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports, ...HlmPaginationImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Depreciation runs</h1>
        <a *appCan="'fixedassets.write'" hlmBtn size="sm" routerLink="/fixed-assets/depreciation-runs/new" class="ms-auto">Run depreciation</a>
        <label class="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" /> Show voided
        </label>
      </div>
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (runs().length === 0) {
        <p class="text-muted-foreground text-sm">No depreciation runs yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>#</th><th hlmTh>Period</th><th hlmTh class="text-right">Total</th><th hlmTh>Status</th></tr></thead>
            <tbody hlmTBody>
              @for (run of runs(); track run.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0" (click)="open(run.id)" (keydown.enter)="open(run.id)">
                  <td hlmTd>{{ run.number ?? '—' }}</td>
                  <td hlmTd>{{ period(run) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(run.total) }}</td>
                  <td hlmTd>{{ run.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="flex items-center justify-between text-sm text-muted-foreground">
          <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
          <nav hlmPagination aria-label="Depreciation runs pagination">
            <ul hlmPaginationContent>
              <li hlmPaginationItem>
                <hlm-pagination-previous
                  [class]="skip() === 0 ? 'pointer-events-none opacity-50' : ''"
                  (click)="prev()"
                />
              </li>
              <li hlmPaginationItem>
                <hlm-pagination-next
                  [class]="currentPage() >= pageCount() ? 'pointer-events-none opacity-50' : ''"
                  (click)="next()"
                />
              </li>
            </ul>
          </nav>
        </div>
      }
    </div>
  `,
})
export class RunList {
  private readonly svc = inject(FixedAssetsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({ id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit() }));

  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, includeVoided, skip, limit }) => {
        if (!id) return of(null);
        return this.svc.listRuns({ skip, limit, includeVoided }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading runs'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<DepreciationRun> | null },
  );

  readonly runs = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  period(r: DepreciationRun): string { return `${r.period.year}-${String(r.period.month).padStart(2, '0')}`; }
  open(id: string): void { void this.router.navigate(['/fixed-assets/depreciation-runs', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  money(n: number): string { return fmtMoney(n); }
}
