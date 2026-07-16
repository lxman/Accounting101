import { Directive } from '@angular/core';

/** Truncates a text element to one line with an ellipsis. Apply to an inner element
 * (e.g. `<span appTruncate>`) inside a table cell or flex row; the element takes the
 * cell/flex width and clips overflow. No tooltip — every site this is applied to has
 * a row that drills into a detail showing the full value. */
@Directive({
  selector: '[appTruncate]',
  host: { class: 'block truncate min-w-0' },
})
export class TruncateDirective {}
