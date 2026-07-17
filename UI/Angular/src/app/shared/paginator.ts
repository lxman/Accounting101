import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { HlmPaginationImports } from '@spartan-ng/helm/pagination';

/**
 * Shared list paginator: "Page X of Y", a rows-per-page selector, and Spartan
 * hlm-pagination previous/next. Presentational — the parent owns skip/limit state
 * and reacts to (previous)/(next)/(pageSizeChange).
 *
 * Rendered as the fixed-height footer of a list's content column. The `-mb-[30px]`
 * pulls the bar down through the list's `p-4` (16px) + main's `p-6` (24px) = 40px of
 * stacked bottom padding, leaving a deliberate ~10px gap above the page edge.
 */
@Component({
  selector: 'app-paginator',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmPaginationImports],
  // display:contents collapses this host box so the bar is a direct flex child of the
  // list's content column, sitting as its fixed-height footer at the bottom.
  host: { class: 'contents' },
  template: `
    <div class="-mb-[30px] flex h-10 shrink-0 items-center justify-between gap-4 border-t border-border bg-background text-sm text-muted-foreground">
      <span class="whitespace-nowrap">Page {{ currentPage() }} of {{ pageCount() }}</span>
      <div class="flex items-center gap-4">
        <label class="flex items-center gap-2 whitespace-nowrap">
          <span>Rows per page</span>
          <select
            aria-label="Rows per page"
            class="rounded-md border border-border bg-background px-2 py-1 text-foreground"
            (change)="pageSizeChange.emit(+$any($event.target).value)"
          >
            @for (o of options(); track o) {
              <option [value]="o" [selected]="o === pageSize()">{{ o }}</option>
            }
          </select>
        </label>
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
    </div>
  `,
})
export class Paginator {
  readonly currentPage = input.required<number>();
  readonly pageCount = input.required<number>();
  readonly ariaLabel = input('Pagination');
  readonly pageSize = input(50);
  readonly pageSizeOptions = input<number[]>([25, 50, 100, 200]);
  readonly previous = output<void>();
  readonly next = output<void>();
  readonly pageSizeChange = output<number>();
  protected readonly atStart = computed(() => this.currentPage() <= 1);
  protected readonly atEnd = computed(() => this.currentPage() >= this.pageCount());
  // Always include the active page size so a non-standard value (e.g. 20) still renders selected.
  protected readonly options = computed(() =>
    [...new Set([...this.pageSizeOptions(), this.pageSize()])].sort((a, b) => a - b));
}
