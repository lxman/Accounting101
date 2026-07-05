import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  signal,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ClientContextService } from '../../core/client/client-context.service';
import { EntriesService } from '../../core/entries/entries.service';
import { EntryResponse, Posting } from '../../core/entries/entry';
import { PagedResponse } from '../../core/api/paged-response';
import { displayDate } from '../../core/format/display';
import { PostingBadge } from '../../shared/posting-badge';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-entry-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    HlmButton,
    PostingBadge,
    CanDirective,
    ...HlmTableImports,
    ...HlmSelectImports,
    ...HlmPaginationImports,
  ],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Journal Entries</h1>
        <a *appCan="'gl.post'" hlmBtn size="sm" routerLink="/journal/new" class="ms-auto">New entry</a>
        <div hlmSelect (valueChange)="onPostingChange($event)">
          <hlm-select-trigger class="w-48">
            <hlm-select-value placeholder="All postings" />
          </hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            <hlm-select-item value="">All</hlm-select-item>
            <hlm-select-item value="Posted">Posted</hlm-select-item>
            <hlm-select-item value="PendingApproval">Pending Approval</hlm-select-item>
          </hlm-select-content>
        </div>
      </div>

      @if (loading()) {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }

      @if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      }

      @if (!loading() && !error()) {
        @if (entries().length === 0) {
          <p class="text-muted-foreground text-sm">No entries found.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>#</th>
                  <th hlmTh>Date</th>
                  <th hlmTh>Memo</th>
                  <th hlmTh>Lines</th>
                  <th hlmTh>Status</th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (entry of entries(); track entry.id) {
                  <tr hlmTr class="cursor-pointer hover:bg-muted/50"
                      tabindex="0"
                      (click)="open(entry.id)"
                      (keydown.enter)="open(entry.id)">
                    <td hlmTd>{{ entry.sequenceNumber }}</td>
                    <td hlmTd>{{ formatDate(entry.effectiveDate) }}</td>
                    <td hlmTd>{{ entry.memo ?? '—' }}</td>
                    <td hlmTd>{{ entry.lineCount }}</td>
                    <td hlmTd><app-posting-badge [posting]="entry.posting" /></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <div class="flex items-center justify-between text-sm text-muted-foreground">
            <span class="whitespace-nowrap">Page {{ currentPage() }} of {{ pageCount() }}</span>
            <nav hlmPagination aria-label="Entries pagination">
              <ul hlmPaginationContent>
                <li hlmPaginationItem>
                  <hlm-pagination-previous
                    [class]="skip() === 0 ? 'pointer-events-none opacity-50' : ''"
                    (click)="prevPage()"
                  />
                </li>
                <li hlmPaginationItem>
                  <hlm-pagination-next
                    [class]="currentPage() >= pageCount() ? 'pointer-events-none opacity-50' : ''"
                    (click)="nextPage()"
                  />
                </li>
              </ul>
            </nav>
          </div>
        }
      }
    </div>
  `,
})
export class EntryList {
  private readonly entriesSvc = inject(EntriesService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  open(id: string): void {
    void this.router.navigate(['/journal', id]);
  }

  readonly posting = signal<Posting | null>(null);
  readonly skip = signal(0);
  readonly limit = signal(50);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  // The query: re-emits whenever the client id or any filter/page signal changes.
  private readonly query = computed(() => ({
    id: this.client.clientId(),
    posting: this.posting() ?? undefined,
    skip: this.skip(),
    limit: this.limit(),
  }));

  // Reactive load: switchMap cancels any in-flight request when the query changes,
  // so a slow response can never overwrite newer filtered data, and there is no
  // unmanaged subscription (toSignal is tied to the injection context).
  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => {
        this.loading.set(true);
        this.error.set(null);
      }),
      switchMap(({ id, posting, skip, limit }) => {
        if (!id) {
          this.loading.set(false);
          return of(null);
        }
        return this.entriesSvc.listPaged({ posting, skip, limit }).pipe(
          tap(() => this.loading.set(false)),
          catchError((e: unknown) => {
            this.error.set((e as { message?: string })?.message ?? 'Error loading entries');
            this.loading.set(false);
            return of(null);
          }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<EntryResponse> | null },
  );

  readonly entries = computed(() => this.page()?.items ?? []);
  readonly pageCount = computed(() => {
    const p = this.page();
    if (!p || p.total === 0) return 1;
    return Math.ceil(p.total / p.limit);
  });
  readonly currentPage = computed(() => {
    const p = this.page();
    if (!p) return 1;
    return Math.floor(p.skip / p.limit) + 1;
  });

  onPostingChange(value: unknown): void {
    const v = value as string;
    this.posting.set(v === '' ? null : (v as Posting));
    this.skip.set(0);
  }

  prevPage(): void {
    const s = this.skip();
    const l = this.limit();
    if (s > 0) this.skip.set(Math.max(0, s - l));
  }

  nextPage(): void {
    const s = this.skip();
    const l = this.limit();
    if (this.currentPage() < this.pageCount()) this.skip.set(s + l);
  }

  formatDate(date: string): string {
    return displayDate(date);
  }
}
