import { ChangeDetectionStrategy, Component } from '@angular/core';
import { ChartHealthWidget } from './chart-health-widget';

@Component({
  selector: 'app-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ChartHealthWidget],
  template: `<h1 class="text-2xl font-bold mb-2">Dashboard</h1>
    <p class="text-muted-foreground mb-4">Welcome to Accounting 101.</p>
    <app-chart-health-widget />`,
})
export class Dashboard {}
