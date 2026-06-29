import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1 class="text-2xl font-bold mb-2">Dashboard</h1>
    <p class="text-muted-foreground">Welcome to Accounting 101. Accounting screens arrive in the next slice.</p>`,
})
export class Dashboard {}
