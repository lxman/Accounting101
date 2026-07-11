import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChartHealthService } from './chart-health.service';
import { ClientContextService } from '../client/client-context.service';
import { ModuleHealth } from './chart-health';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(ChartHealthService), ctrl: TestBed.inject(HttpTestingController) };
}

function report(moduleKey: string, ready: boolean) {
  return { moduleKey, ready, accounts: [] };
}

describe('ChartHealthService', () => {
  it('fans out to all six module chart-readiness endpoints in order', () => {
    const { svc, ctrl } = setup();
    let out: ModuleHealth[] = [];
    svc.readiness().subscribe(m => (out = m));

    for (const key of ['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory']) {
      ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush(report(key, true));
    }

    expect(out.map(m => m.key)).toEqual(['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory']);
    expect(out.every(m => m.report?.ready && !m.errored)).toBe(true);
    ctrl.verify();
  });

  it('marks a module errored when its host call fails, keeping the others', () => {
    const { svc, ctrl } = setup();
    let out: ModuleHealth[] = [];
    svc.readiness().subscribe(m => (out = m));

    ctrl.expectOne('http://localhost:5000/clients/C1/receivables/chart-readiness').flush(report('receivables', true));
    ctrl.expectOne('http://localhost:5000/clients/C1/payables/chart-readiness').flush(report('payables', false));
    ctrl.expectOne('http://localhost:5000/clients/C1/payroll/chart-readiness')
      .flush('boom', { status: 400, statusText: 'Bad Request' });
    ctrl.expectOne('http://localhost:5000/clients/C1/cash/chart-readiness').flush(report('cash', true));
    ctrl.expectOne('http://localhost:5000/clients/C1/fixedassets/chart-readiness').flush(report('fixedassets', true));
    ctrl.expectOne('http://localhost:5000/clients/C1/inventory/chart-readiness').flush(report('inventory', true));

    const payroll = out.find(m => m.key === 'payroll')!;
    expect(payroll.errored).toBe(true);
    expect(payroll.report).toBeNull();
    expect(out.filter(m => !m.errored).length).toBe(5);
    ctrl.verify();
  });
});
