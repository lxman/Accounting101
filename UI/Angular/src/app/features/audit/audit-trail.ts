import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ClientContextService } from '../../core/client/client-context.service';
import { AuditService } from '../../core/audit/audit.service';
import { AuditRecordResponse } from '../../core/audit/audit';
import { PagedResponse } from '../../core/api/paged-response';
import { CapabilityService } from '../../core/capabilities/capability.service';
import { displayDate } from '../../core/format/display';
import { Paginator } from '../../shared/paginator';

@Component({
  selector: 'app-audit-trail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports, Paginator],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Audit Trail</h1>

      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (!loading() && !error()) {
        @if (records().length === 0) {
          <p class="text-muted-foreground text-sm">No audit activity.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr><th hlmTh>#</th><th hlmTh>Date</th><th hlmTh>Action</th><th hlmTh>Actor</th><th hlmTh>Reason</th><th hlmTh>Entry</th></tr>
              </thead>
              <tbody hlmTBody>
                @for (r of records(); track r.sequence) {
                  <tr hlmTr
                      [class]="canOpen(r) ? 'cursor-pointer hover:bg-muted/50' : ''"
                      [attr.role]="canOpen(r) ? 'button' : null"
                      [attr.tabindex]="canOpen(r) ? 0 : null"
                      (click)="canOpen(r) && open(r)"
                      (keydown.enter)="canOpen(r) && open(r)">
                    <td hlmTd>{{ r.sequence }}</td>
                    <td hlmTd>{{ formatDate(r.at) }}</td>
                    <td hlmTd>{{ r.action }}</td>
                    <td hlmTd>{{ r.actor.name ?? r.actor.userId }}</td>
                    <td hlmTd>{{ r.reason ?? '—' }}</td>
                    <td hlmTd>{{ canOpen(r) ? '↗' : '—' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <app-paginator [currentPage]="currentPage()" [pageCount]="pageCount()" ariaLabel="Audit pagination" (previous)="prevPage()" (next)="nextPage()" [pageSize]="limit()" (pageSizeChange)="setPageSize($event)" />
        }
      }
    </div>
  `,
})
export class AuditTrail {
  private readonly svc = inject(AuditService);
  private readonly client = inject(ClientContextService);
  private readonly caps = inject(CapabilityService);
  private readonly router = inject(Router);

  readonly skip = signal(0);
  readonly limit = signal(50);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  private readonly canDrill = computed(() => this.caps.has('gl.read'));

  private readonly query = computed(() => ({ id: this.client.clientId(), skip: this.skip(), limit: this.limit() }));

  private readonly pageData = toSignal(
    toObservable(this.query).pipe(
      tap(() => { this.loading.set(true); this.error.set(null); }),
      switchMap(({ id, skip, limit }) => {
        if (!id) { this.loading.set(false); return of(null); }
        return this.svc.clientAudit(skip, limit).pipe(
          tap(() => this.loading.set(false)),
          catchError((e: unknown) => {
            this.error.set((e as { message?: string })?.message ?? 'Error loading audit trail');
            this.loading.set(false);
            return of(null);
          }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<AuditRecordResponse> | null },
  );

  readonly records = computed(() => this.pageData()?.items ?? []);
  readonly pageCount = computed(() => {
    const p = this.pageData();
    if (!p || p.total === 0) return 1;
    return Math.ceil(p.total / p.limit);
  });
  readonly currentPage = computed(() => {
    const p = this.pageData();
    if (!p) return 1;
    return Math.floor(p.skip / p.limit) + 1;
  });

  // An audit record's entryId is a polymorphic subject id: for journal-entry actions it IS a journal
  // entry, but for AccountCreated/AccountUpdated it is the account id and for Document* it is the document
  // id — neither of which resolves at /journal/:id (that drill would 404). Only journal-entry actions drill.
  private static readonly JournalEntryActions = new Set(['Created', 'Approved', 'Superseded', 'Voided', 'Reversed']);

  canOpen(r: AuditRecordResponse): boolean {
    return this.canDrill() && !!r.entryId && AuditTrail.JournalEntryActions.has(r.action);
  }
  open(r: AuditRecordResponse): void { if (this.canOpen(r)) void this.router.navigate(['/journal', r.entryId]); }

  prevPage(): void { const s = this.skip(), l = this.limit(); if (s > 0) this.skip.set(Math.max(0, s - l)); }
  nextPage(): void { const s = this.skip(), l = this.limit(); if (this.currentPage() < this.pageCount()) this.skip.set(s + l); }
  setPageSize(n: number): void { this.limit.set(n); this.skip.set(0); }

  formatDate(d: string): string { return displayDate(d); }
}
