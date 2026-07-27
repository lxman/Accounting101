import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BankStatement } from '../../core/banking/banking';
import { money as fmtMoney } from '../../core/format/display';

/**
 * The advisory duplicate block shared by the statement editor and the statement import screen: statements
 * already on file for the same cash account and statement date. It never blocks — re-issued and corrected
 * statements are legitimate and only a human can tell.
 */
@Component({
  selector: 'app-statement-duplicate-warning',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    @if (duplicates().length) {
      <div class="border border-amber-400 rounded p-3 text-sm flex flex-col gap-1">
        <p class="font-semibold">Already on file for this account and date</p>
        <p class="text-muted-foreground">Recording this statement is still allowed.</p>
        <ul class="list-disc ps-5">
          @for (s of duplicates(); track s.id) {
            <li>
              <a [routerLink]="['/cash/statements', s.id]" class="text-primary hover:underline">{{ s.number ?? 'Statement' }}</a>
              — {{ s.statementDate }}, closing {{ money(s.closingBalance) }}
            </li>
          }
        </ul>
      </div>
    }
  `,
})
export class StatementDuplicateWarning {
  readonly duplicates = input.required<BankStatement[]>();
  money(n: number): string { return fmtMoney(n); }
}
