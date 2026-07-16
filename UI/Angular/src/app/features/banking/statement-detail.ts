import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatement } from '../../core/banking/banking';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-statement-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/cash/statements" class="text-sm text-muted-foreground hover:text-foreground">← Statements</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (statement(); as s) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Statement {{ s.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="s.status === 'Void'">{{ s.status }}</span>
          @if (s.status === 'Posted') {
            <button *appCan="'bankrec.write'" hlmBtn size="sm" class="ms-auto" (click)="startReconciliation(s.id)">Start reconciliation</button>
          }
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Statement date</td><td class="text-right">{{ date(s.statementDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Opening balance</td><td class="text-right tabular-nums">{{ money(s.openingBalance) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Closing balance</td><td class="text-right tabular-nums">{{ money(s.closingBalance) }}</td></tr>
          </tbody>
        </table>

        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>Date</th><th hlmTh>Description</th><th hlmTh class="text-right">Amount</th><th hlmTh>Ref</th></tr></thead>
            <tbody hlmTBody>
              @for (l of s.lines; track $index) {
                <tr hlmTr><td hlmTd>{{ date(l.date) }}</td><td hlmTd><span class="whitespace-normal break-words">{{ l.description }}</span></td>
                  <td hlmTd class="text-right tabular-nums" [class.text-destructive]="l.amount < 0">{{ money(l.amount) }}</td>
                  <td hlmTd><span class="whitespace-normal break-words">{{ l.externalRef ?? '' }}</span></td></tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class StatementDetail {
  private readonly svc = inject(BankingService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly statement = signal<BankStatement | null>(null);
  readonly message = signal<string | null>(null);

  constructor() {
    this.svc.getStatement(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => this.statement.set(s),
      error: (e) => this.message.set(extractProblem(e).detail),
    });
  }

  // BK-2: navigate to the reconcile tab with the statement id; BK-4 turns this into a real start.
  startReconciliation(statementId: string): void {
    void this.router.navigate(['/cash/reconciliation'], { queryParams: { statement: statementId } });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
