import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { Dashboard } from './dashboard';

describe('Dashboard', () => {
  it('does not render the chart-health widget', () => {
    // The widget moved to a Module Setup screen; the dashboard no longer hosts it.
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
    const f = TestBed.createComponent(Dashboard); f.detectChanges();
    expect((f.nativeElement as HTMLElement).querySelector('app-chart-health-widget')).toBeNull();
  });
});
