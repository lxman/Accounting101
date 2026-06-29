import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-placeholder',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="text-muted-foreground">Coming soon.</div>`,
})
export class Placeholder {}
