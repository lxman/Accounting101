import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Dashboard } from './dashboard';

describe('Dashboard', () => {
  it('renders the chart-health widget', () => {
    // No client selected → the widget's load effect early-returns, so no HTTP is issued.
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const f = TestBed.createComponent(Dashboard); f.detectChanges();
    expect((f.nativeElement as HTMLElement).querySelector('app-chart-health-widget')).not.toBeNull();
  });
});
