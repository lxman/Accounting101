import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunList } from './run-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('FA RunList', () => {
  it('renders runs with period and total', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(RunList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/depreciation-runs').flush({
      items: [{ run: { id: 'r1', number: 'DR-00001', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31',
        memo: null, lines: [], total: 1500, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('DR-00001');
    expect(f.nativeElement.textContent).toContain('2026-01');
    expect(f.nativeElement.textContent).toContain('1,500');
    ctrl.verify();
  });
});
