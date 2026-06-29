import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { EntriesService } from '../../core/entries/entries.service';
import { EntryResponse } from '../../core/entries/entry';
import { AuditService } from '../../core/audit/audit.service';
import { AuditRecordResponse } from '../../core/audit/audit';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';
import { formatMoney } from '../../core/format/money-formatter';
import { formatProfileDate } from '../../core/format/date-formatter';

@Component({
  selector: 'app-entry-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports, ...HlmBadgeImports, HlmButton, ...HlmInputImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      @if (entry(); as e) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Entry #{{ e.sequenceNumber }}</h1>
          @if (e.posting === 'PendingApproval') { <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]">Pending</span> }
          @else { <span hlmBadge variant="secondary">{{ e.posting }}</span> }
        </div>
        <div class="text-sm text-muted-foreground">
          {{ formatDate(e.effectiveDate) }} · {{ e.type }} · {{ e.reference ?? '—' }} · {{ e.memo ?? '' }}
        </div>
        @if (e.sourceRef) {
          <div class="text-sm text-muted-foreground">Source: {{ e.sourceType }} · {{ e.sourceRef }}</div>
        }

        <div hlmTableContainer><table hlmTable>
          <thead hlmTHead><tr hlmTr><th hlmTh>Account</th><th hlmTh class="text-right">Debit</th><th hlmTh class="text-right">Credit</th></tr></thead>
          <tbody hlmTBody>
            @for (l of e.lines; track $index) {
              <tr hlmTr>
                <td hlmTd>{{ accountLabel(l.accountId) }}</td>
                <td hlmTd class="text-right tabular-nums">{{ l.direction === 'Debit' ? money(l.amount) : '' }}</td>
                <td hlmTd class="text-right tabular-nums">{{ l.direction === 'Credit' ? money(l.amount) : '' }}</td>
              </tr>
            }
          </tbody>
          <tfoot><tr hlmTr class="font-semibold border-double border-t-4 border-border">
            <td hlmTd class="text-right">Totals</td>
            <td hlmTd class="text-right tabular-nums">{{ money(totalDebit()) }}</td>
            <td hlmTd class="text-right tabular-nums">{{ money(totalCredit()) }}</td>
          </tr></tfoot>
        </table></div>

        <div class="text-sm">
          <h2 class="font-semibold">Audit</h2>
          @for (r of audit(); track r.sequence) {
            <div class="text-muted-foreground">{{ r.action }} by {{ r.actor.name ?? r.actor.userId }} at {{ formatDate(r.at) }}</div>
          }
        </div>

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

        @if (e.posting === 'PendingApproval') {
          <div class="flex items-center gap-2">
            <button hlmBtn type="button" (click)="approve()" [disabled]="busy()">Approve</button>
            <input hlmInput type="text" placeholder="Void reason" [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
            <button hlmBtn type="button" variant="outline" (click)="voidEntry()" [disabled]="busy()">Void</button>
          </div>
        }
      } @else if (loadError()) { <p class="text-destructive text-sm">{{ loadError() }}</p> }
      @else { <p class="text-muted-foreground text-sm">Loading…</p> }
    </div>
  `,
})
export class EntryDetail {
  private readonly entries = inject(EntriesService);
  private readonly auditSvc = inject(AuditService);
  private readonly accounts = inject(AccountsService);
  private readonly route = inject(ActivatedRoute);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly entry = signal<EntryResponse | null>(null);
  readonly audit = signal<AuditRecordResponse[]>([]);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly loadError = signal<string | null>(null);

  readonly totalDebit = computed(() => (this.entry()?.lines ?? []).filter(l => l.direction === 'Debit').reduce((s, l) => s + l.amount, 0));
  readonly totalCredit = computed(() => (this.entry()?.lines ?? []).filter(l => l.direction === 'Credit').reduce((s, l) => s + l.amount, 0));

  readonly voidReason = signal('');

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); this.load(); }

  private load(): void {
    this.entries.get(this.id).subscribe({ next: (e) => this.entry.set(e), error: (e) => this.loadError.set(extractProblem(e).detail) });
    this.auditSvc.entryAudit(this.id).subscribe({ next: (a) => this.audit.set(a) });
  }

  accountLabel(id: string): string { return this.accounts.label(id); }
  money(n: number): string { return formatMoney(n, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false }); }
  formatDate(d: string): string { return formatProfileDate(d, DEFAULT_FORMAT_PROFILE); }

  approve(): void {
    this.busy.set(true); this.message.set(null);
    this.entries.approve(this.id).subscribe({
      next: () => { this.busy.set(false); this.load(); },
      error: (err) => { this.message.set(extractProblem(err).detail); this.busy.set(false); },
    });
  }

  voidEntry(): void {
    this.busy.set(true); this.message.set(null);
    this.entries.void(this.id, this.voidReason() || undefined).subscribe({
      next: () => { this.busy.set(false); this.load(); },
      error: (err) => { this.message.set(extractProblem(err).detail); this.busy.set(false); },
    });
  }
}
