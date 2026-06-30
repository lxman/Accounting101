import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { SettlementStatus } from '../core/receivables/receivables';

@Component({
  selector: 'app-settlement-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmBadgeImports],
  template: `
    @switch (status()) {
      @case ('Open')          { <span hlmBadge variant="outline" data-testid="badge-open">Open</span> }
      @case ('PartiallyPaid') { <span hlmBadge variant="secondary" data-testid="badge-partial">Partial</span> }
      @case ('Paid')          { <span hlmBadge variant="secondary" data-testid="badge-paid">Paid</span> }
    }
  `,
})
export class SettlementBadge { readonly status = input.required<SettlementStatus>(); }
