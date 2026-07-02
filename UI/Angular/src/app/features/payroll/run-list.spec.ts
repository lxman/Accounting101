import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunList } from './run-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('payroll.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RunList', () => {
  it('renders payroll runs with computed net pay', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/payroll-runs')
      .flush({ items: [{ run: { id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5,
        deductions: 50, incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Posted' } }],
        total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    // net pay = 1000 - 76.5 - 120 - 50 = 753.50
    expect(f.nativeElement.textContent).toContain('PR-1');
    expect(f.nativeElement.textContent).toContain('753.50');
    ctrl.verify();
  });

  it('hides "Record payroll run" without payroll.write', () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities()],
    });
    TestBed.inject(ClientContextService).select('C1');
    const ctrl = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(RunList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/payroll-runs')
      .flush({ items: [], total: 0, skip: 0, limit: 50 });
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).not.toContain('Record payroll run');
    ctrl.verify();
  });
});
