import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { RefundView } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';

@Component({
  selector: 'app-refund-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables/refunds" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Refunds</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Refund</h1>
          <span class="text-sm px-2 py-0.5 rounded border border-border">{{ v.refund.voided ? 'Voided' : 'Active' }}</span>
        </div>
        <div class="text-sm flex flex-col gap-1">
          <div><span class="text-muted-foreground">Date</span> · {{ formatDate(v.refund.date) }}</div>
          <div><span class="text-muted-foreground">Amount</span> · <span class="tabular-nums">{{ money(v.refund.amount) }}</span></div>
          <div><span class="text-muted-foreground">Memo</span> · {{ v.refund.memo ?? '—' }}</div>
        </div>
        @if (v.journalEntryId) {
          <a [routerLink]="['/journal', v.journalEntryId]" class="text-sm text-primary hover:underline w-fit">View journal entry →</a>
        }
      } @else if (loadError()) {
        <p class="text-destructive text-sm">{{ loadError() }}</p>
      } @else {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }
    </div>
  `,
})
export class RefundDetail {
  private readonly svc = inject(ReceivablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<RefundView | null>(null);
  readonly loadError = signal<string | null>(null);

  constructor() {
    this.svc.getRefund(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => this.view.set(v),
      error: (e) => this.loadError.set(extractProblem(e).detail),
    });
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
