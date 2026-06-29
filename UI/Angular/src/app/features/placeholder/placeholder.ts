import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-placeholder',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="text-[color:var(--color-muted)]">Coming soon.</div>`,
})
export class Placeholder {}
