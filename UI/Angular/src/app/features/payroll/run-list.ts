import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayrollService } from '../../core/payroll/payroll.service';
import { PayrollRun, netPay } from '../../core/payroll/payroll';
import { PagedResponse } from '../../core/api/paged-response';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { Paginator } from '../../shared/paginator';

@Component({
  selector: 'app-run-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports, Paginator],
  template: `
    <div class="flex flex-col gap-4 p-4 flex-1 min-h-0">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Payroll runs</h1>
        <a *appCan="'payroll.write'" hlmBtn size="sm" routerLink="/payroll/runs/new" class="ms-auto">Record payroll run</a>
        <label class="flex items-center gap-2 text-sm text-muted-foreground">
          <input type="checkbox" [checked]="includeVoided()" (change)="toggleVoided($any($event.target).checked)" />
          Show voided
        </label>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (runs().length === 0) {
        <p class="text-muted-foreground text-sm">No payroll runs yet.</p>
      } @else {
        <div hlmTableContainer class="flex-1 min-h-0 overflow-y-auto">
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>#</th><th hlmTh>Pay date</th>
                <th hlmTh class="text-right">Gross</th><th hlmTh class="text-right">Net pay</th><th hlmTh>Status</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (run of runs(); track run.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0"
                    (click)="open(run.id)" (keydown.enter)="open(run.id)">
                  <td hlmTd>{{ run.number ?? '—' }}</td>
                  <td hlmTd>{{ formatDate(run.payDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(run.gross) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(net(run)) }}</td>
                  <td hlmTd>{{ run.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Payroll runs pagination" (previous)="prev()" (next)="next()" [pageSize]="limit()" (pageSizeChange)="setPageSize($event)" />
      }
    </div>
  `,
})
export class RunList {
  private readonly svc = inject(PayrollService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly includeVoided = signal(false);
  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly error = signal<string | null>(null);

  private readonly query = computed(() => ({
    id: this.client.clientId(), includeVoided: this.includeVoided(), skip: this.skip(), limit: this.limit(),
  }));

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
    { initialValue: null as PagedResponse<PayrollRun> | null },
  );

  readonly runs = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => { const p = this.page(); return !p || p.total === 0 ? 1 : Math.ceil(p.total / p.limit); });
  readonly currentPage = computed(() => { const p = this.page(); return !p ? 1 : Math.floor(p.skip / p.limit) + 1; });

  net(r: PayrollRun): number { return netPay(r); }
  open(id: string): void { void this.router.navigate(['/payroll/runs', id]); }
  toggleVoided(v: boolean): void { this.includeVoided.set(v); this.skip.set(0); }
  prev(): void { if (this.skip() > 0) this.skip.set(Math.max(0, this.skip() - this.limit())); }
  next(): void { if (this.currentPage() < this.pageCount()) this.skip.set(this.skip() + this.limit()); }
  setPageSize(n: number): void { this.limit.set(n); this.skip.set(0); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
