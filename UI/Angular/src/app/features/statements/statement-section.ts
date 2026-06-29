import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatementSection as StatementSectionModel } from '../../core/statements/statement';
import { formatMoney, isNegativeAmount } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';

@Component({
  selector: 'app-statement-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="mb-4">
      <h3 class="text-base font-semibold mb-1">{{ section().title }}</h3>
      <table class="w-full text-sm">
        <tbody>
          @for (line of section().lines; track line.accountId ?? $index) {
            <tr>
              <td class="py-0.5 pr-4">
                @if (line.number) {
                  <span class="text-muted-foreground mr-1">{{ line.number }}</span>
                }
                {{ line.name }}
              </td>
              <td
                class="text-end tabular-nums py-0.5 w-32"
                [class.text-destructive]="isNeg(line.amount)">
                {{ fmt(line.amount, false) }}
              </td>
            </tr>
          }
        </tbody>
        <tfoot>
          <tr class="border-t border-foreground font-semibold">
            <td class="pt-1 pr-4">Total {{ section().title }}</td>
            <td
              class="text-end tabular-nums pt-1 w-32"
              [class.text-destructive]="isNeg(section().total)"
              data-testid="section-total">
              {{ fmt(section().total, true) }}
            </td>
          </tr>
        </tfoot>
      </table>
    </div>
  `,
})
export class StatementSectionComponent {
  readonly section = input.required<StatementSectionModel>();

  fmt(amount: number, symbol: boolean): string {
    return formatMoney(amount, 'USD', DEFAULT_FORMAT_PROFILE, { symbol });
  }

  isNeg(amount: number): boolean {
    return isNegativeAmount(amount);
  }
}
