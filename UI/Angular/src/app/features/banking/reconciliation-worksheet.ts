import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { BankingService } from '../../core/banking/banking.service';
import { ReconciliationWorksheet as Worksheet, WorksheetEntry, AutoMatchProposal } from '../../core/banking/banking';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { AdjustmentsPanel } from './adjustments-panel';

@Component({
  selector: 'app-reconciliation-worksheet',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, AdjustmentsPanel, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-4xl">
      <a routerLink="/cash/reconciliation" class="text-sm text-muted-foreground hover:text-foreground">← Reconcile</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (worksheet(); as w) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Reconciliation {{ w.reconciliation.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded">{{ w.reconciliation.status }}</span>
          <a [routerLink]="['/cash/statements', w.statement.id]" class="text-sm text-primary hover:underline ms-auto">Statement {{ w.statement.number }} →</a>
        </div>

        <div class="grid grid-cols-4 gap-3 text-sm">
          <div class="border border-border rounded p-2"><div class="text-muted-foreground">Book balance</div><div class="tabular-nums text-lg">{{ money(w.bookBalance) }}</div></div>
          <div class="border border-border rounded p-2"><div class="text-muted-foreground">Cleared total</div><div class="tabular-nums text-lg">{{ money(w.clearedTotal) }}</div></div>
          <div class="border border-border rounded p-2"><div class="text-muted-foreground">Statement closing</div><div class="tabular-nums text-lg">{{ money(w.statement.closingBalance) }}</div></div>
          <div class="border border-border rounded p-2" [class.border-emerald-500]="w.balanced" [class.border-destructive]="!w.balanced">
            <div class="text-muted-foreground">Difference</div>
            <div class="tabular-nums text-lg" [class.text-emerald-600]="w.balanced" [class.text-destructive]="!w.balanced">{{ money(w.reconciledDifference) }}</div>
          </div>
        </div>

        @if (w.reconciliation.status === 'InProgress') {
          <div class="flex items-center gap-2">
            <button *appCan="'bankrec.write'" hlmBtn size="sm" variant="outline" (click)="loadProposal()" [disabled]="busy()">Auto-match</button>
            <button *appCan="'bankrec.write'" hlmBtn size="sm" (click)="complete()" [disabled]="!w.balanced || busy()">Complete</button>
          </div>
        }

        @if (proposal(); as p) {
          <div class="border border-border rounded p-3 flex flex-col gap-2 text-sm">
            <div class="flex items-center gap-3">
              <span class="font-semibold">Auto-match proposal</span>
              <span class="text-muted-foreground">{{ p.matchedEntryIds.length }} entr(ies) match · {{ p.unmatchedStatementLines.length }} statement line(s) unmatched</span>
              <div class="ms-auto flex gap-2">
                <button *appCan="'bankrec.write'" hlmBtn size="sm" (click)="applyProposal()" [disabled]="p.matchedEntryIds.length === 0 || busy()">Apply</button>
                <button hlmBtn size="sm" variant="ghost" (click)="proposal.set(null)">Dismiss</button>
              </div>
            </div>
          </div>
        }

        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>Cleared</th><th hlmTh>Date</th><th hlmTh>Reference</th>
              <th hlmTh>Source</th><th hlmTh class="text-right">Cash effect</th></tr></thead>
            <tbody hlmTBody>
              @for (e of w.entries; track e.entryId) {
                <tr hlmTr>
                  <td hlmTd><input type="checkbox" [checked]="e.cleared" [disabled]="w.reconciliation.status !== 'InProgress' || busy()" (change)="toggle(e)" /></td>
                  <td hlmTd>{{ date(e.date) }}</td>
                  <td hlmTd>{{ e.reference ?? '—' }}</td>
                  <td hlmTd>{{ e.sourceType ?? '—' }}</td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="e.cashEffect < 0">{{ money(e.cashEffect) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>

        <app-adjustments-panel [reconciliationId]="w.reconciliation.id" [locked]="w.reconciliation.status !== 'InProgress'" (changed)="reload()" />
      }
    </div>
  `,
})
export class ReconciliationWorksheet {
  private readonly svc = inject(BankingService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly worksheet = signal<Worksheet | null>(null);
  readonly proposal = signal<AutoMatchProposal | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getWorksheet(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  toggle(e: WorksheetEntry): void {
    this.busy.set(true); this.message.set(null);
    const call = e.cleared ? this.svc.unclear(this.id, [e.entryId]) : this.svc.clear(this.id, [e.entryId]);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.busy.set(false); },
      error: (err) => { this.message.set(extractProblem(err).detail); this.busy.set(false); },
    });
  }

  loadProposal(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.autoMatchProposal(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (p) => { this.proposal.set(p); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  applyProposal(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.autoMatchApply(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.proposal.set(null); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  complete(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.completeReconciliation(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (w) => { this.worksheet.set(w); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
