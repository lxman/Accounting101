import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
  untracked,
} from '@angular/core';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ClientContextService } from '../../core/client/client-context.service';
import { EntriesService } from '../../core/entries/entries.service';
import { EntryResponse, Posting } from '../../core/entries/entry';
import { PagedResponse } from '../../core/api/paged-response';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { formatProfileDate } from '../../core/format/date-formatter';

@Component({
  selector: 'app-entry-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ...HlmTableImports,
    ...HlmSelectImports,
    ...HlmPaginationImports,
    ...HlmBadgeImports,
  ],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Journal Entries</h1>
        <div hlmSelect (valueChange)="onPostingChange($event)" class="ms-auto">
          <hlm-select-trigger class="w-48">
            <hlm-select-value placeholder="All postings" />
          </hlm-select-trigger>
          <hlm-select-content>
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
                  <tr hlmTr>
                    <td hlmTd>{{ entry.sequenceNumber }}</td>
                    <td hlmTd>{{ formatDate(entry.effectiveDate) }}</td>
                    <td hlmTd>{{ entry.memo ?? '—' }}</td>
                    <td hlmTd>{{ entry.lineCount }}</td>
                    <td hlmTd>
                      @if (entry.posting === 'PendingApproval') {
                        <span hlmBadge
                          class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]"
                          data-testid="badge-pending">
                          Pending
                        </span>
                      } @else {
                        <span hlmBadge variant="secondary" data-testid="badge-posted">
                          Posted
                        </span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <div class="flex items-center justify-between text-sm text-muted-foreground">
            <span>Page {{ currentPage() }} of {{ pageCount() }}</span>
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

  readonly posting = signal<Posting | null>(null);
  readonly skip = signal(0);
  readonly limit = signal(50);

  private readonly page = signal<PagedResponse<EntryResponse> | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

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

  constructor() {
    effect(() => {
      const id = this.client.clientId();
      const posting = this.posting();
      const skip = this.skip();
      const limit = this.limit();

      if (!id) {
        untracked(() => this.page.set(null));
        return;
      }

      untracked(() => {
        this.loading.set(true);
        this.error.set(null);
      });

      this.entriesSvc
        .listPaged({ posting: posting ?? undefined, skip, limit })
        .subscribe({
          next: p => untracked(() => { this.page.set(p); this.loading.set(false); }),
          error: e => untracked(() => {
            this.error.set(e?.message ?? 'Error loading entries');
            this.loading.set(false);
          }),
        });
    });
  }

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
    return formatProfileDate(date, DEFAULT_FORMAT_PROFILE);
  }
}
