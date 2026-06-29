import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmBadgeImports } from '@spartan-ng/helm/badge';
import { Posting } from '../core/entries/entry';

@Component({
  selector: 'app-posting-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmBadgeImports],
  template: `
    @if (posting() === 'PendingApproval') {
      <span hlmBadge class="bg-[color:var(--pending)] text-[color:var(--pending-foreground)]" data-testid="badge-pending">Pending</span>
    } @else {
      <span hlmBadge variant="secondary" data-testid="badge-posted">Posted</span>
    }
  `,
})
export class PostingBadge { readonly posting = input.required<Posting>(); }
