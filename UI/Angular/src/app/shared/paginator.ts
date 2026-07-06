import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';

/**
 * Shared list paginator: "Page X of Y" + Spartan hlm-pagination previous/next.
 * Presentational — the parent owns skip/limit state and reacts to (previous)/(next).
 */
@Component({
  selector: 'app-paginator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmPaginationImports],
  template: `
    <div class="flex items-center justify-between text-sm text-muted-foreground">
      <span class="whitespace-nowrap">Page {{ currentPage() }} of {{ pageCount() }}</span>
      <nav hlmPagination [attr.aria-label]="ariaLabel()">
        <ul hlmPaginationContent>
          <li hlmPaginationItem>
            <hlm-pagination-previous
              [class]="atStart() ? 'pointer-events-none opacity-50' : ''"
              (click)="previous.emit()"
            />
          </li>
          <li hlmPaginationItem>
            <hlm-pagination-next
              [class]="atEnd() ? 'pointer-events-none opacity-50' : ''"
              (click)="next.emit()"
            />
          </li>
        </ul>
      </nav>
    </div>
  `,
})
export class Paginator {
  readonly currentPage = input.required<number>();
  readonly pageCount = input.required<number>();
  readonly ariaLabel = input('Pagination');
  readonly previous = output<void>();
  readonly next = output<void>();
  protected readonly atStart = computed(() => this.currentPage() <= 1);
  protected readonly atEnd = computed(() => this.currentPage() >= this.pageCount());
}
