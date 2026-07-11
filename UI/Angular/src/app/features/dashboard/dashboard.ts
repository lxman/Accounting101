import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1 class="text-2xl font-bold mb-2">Dashboard</h1>
    <p class="text-muted-foreground mb-4">Welcome to Accounting 101.</p>`,
})
export class Dashboard {}
