import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { InvoiceStatus } from '../core/receivables/receivables';

@Component({
  selector: 'app-invoice-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmBadgeImports],
  template: `
    @switch (status()) {
      @case ('Draft')  { <span hlmBadge variant="outline" data-testid="badge-draft">Draft</span> }
      @case ('Issued') { <span hlmBadge variant="secondary" data-testid="badge-issued">Issued</span> }
      @case ('Void')   { <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]" data-testid="badge-void">Void</span> }
    }
  `,
})
export class InvoiceStatusBadge { readonly status = input.required<InvoiceStatus>(); }
