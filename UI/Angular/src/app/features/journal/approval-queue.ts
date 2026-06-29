import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { EntriesService } from '../../core/entries/entries.service';
import { EntryResponse } from '../../core/entries/entry';
import { AuditService } from '../../core/audit/audit.service';
import { DevIdentityService } from '../../core/api/dev-identity.service';
import { extractProblem } from '../../core/api/problem-details';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { formatProfileDate } from '../../core/format/date-formatter';

@Component({
  selector: 'app-approval-queue',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmTableImports, ...HlmBadgeImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Pending Approval</h1>
      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (!loading() && !error()) {
        @if (entries().length === 0) { <p class="text-muted-foreground text-sm">Nothing awaiting approval.</p> }
        @else {
          <div hlmTableContainer><table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>#</th><th hlmTh>Date</th><th hlmTh>Memo</th><th hlmTh>Lines</th><th hlmTh>Approvable</th></tr></thead>
            <tbody hlmTBody>
              @for (e of entries(); track e.id) {
                <tr hlmTr>
                  <td hlmTd><a class="underline" [routerLink]="['/journal', e.id]">{{ e.sequenceNumber }}</a></td>
                  <td hlmTd>{{ formatDate(e.effectiveDate) }}</td>
                  <td hlmTd>{{ e.memo ?? '—' }}</td>
                  <td hlmTd>{{ e.lineCount }}</td>
                  <td hlmTd>
                    @if (approvableById()[e.id]) { <span hlmBadge variant="secondary">Approvable</span> }
                    @else { <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]">Your entry — needs another approver</span> }
                  </td>
                </tr>
              }
            </tbody>
          </table></div>
        }
      }
    </div>
  `,
})
export class ApprovalQueue {
  private readonly entriesSvc = inject(EntriesService);
  private readonly audit = inject(AuditService);
  private readonly identity = inject(DevIdentityService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly entries = signal<EntryResponse[]>([]);
  private readonly authorById = signal<Record<string, string | null>>({});

  readonly approvableById = computed(() => {
    const me = this.identity.active().sub; const authors = this.authorById();
    return Object.fromEntries(this.entries().map(e => [e.id, authors[e.id] != null && authors[e.id] !== me]));
  });

  constructor() {
    this.entriesSvc.listPaged({ posting: 'PendingApproval', skip: 0, limit: 50 }).subscribe({
      next: (page) => {
        this.entries.set(page.items); this.loading.set(false);
        if (page.items.length === 0) return;
        forkJoin(Object.fromEntries(page.items.map(e => [e.id, this.audit.entryAudit(e.id)]))).subscribe({
          next: (map) => this.authorById.set(Object.fromEntries(Object.entries(map).map(([id, recs]) => [id, this.audit.authorOf(recs)]))),
          error: () => { /* cue only; leave authors empty → rows show not-approvable, safe default */ },
        });
      },
      error: (e) => { this.error.set(extractProblem(e).detail); this.loading.set(false); },
    });
  }

  formatDate(d: string): string { return formatProfileDate(d, DEFAULT_FORMAT_PROFILE); }
}
