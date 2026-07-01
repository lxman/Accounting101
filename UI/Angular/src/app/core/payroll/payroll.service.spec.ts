import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PayrollService } from './payroll.service';
import { ClientContextService } from '../client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(PayrollService), ctrl: TestBed.inject(HttpTestingController) };
}

describe('PayrollService', () => {
  it('lists runs, unwrapping the PayrollRunView envelope', () => {
    const { svc, ctrl } = setup();
    let items: unknown;
    svc.listRuns({ skip: 0, limit: 50, includeVoided: true }).subscribe(p => (items = p.items));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/payroll-runs'
      && r.params.get('skip') === '0' && r.params.get('limit') === '50' && r.params.get('includeVoided') === 'true');
    req.flush({ items: [{ run: { id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5,
      deductions: 50, incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Posted' } }],
      total: 1, skip: 0, limit: 50 });
    expect(items).toEqual([{ id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5,
      deductions: 50, incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Posted' }]);
    ctrl.verify();
  });

  it('records a run (posts the request body verbatim)', () => {
    const { svc, ctrl } = setup();
    const body = { gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: 'June' };
    svc.recordRun(body).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({ id: 'r9', number: 'PR-9', ...body, status: 'Posted' });
    ctrl.verify();
  });

  it('voids a run with a reason', () => {
    const { svc, ctrl } = setup();
    svc.voidRun('r1', 'mistake').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs/r1/void');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'mistake' });
    req.flush({ id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 0, employerFica: 0, deductions: 0,
      incomeTaxWithheld: 0, payDate: '2026-06-30', memo: null, status: 'Void' });
    ctrl.verify();
  });

  it('gets a remittance, unwrapping the view', () => {
    const { svc, ctrl } = setup();
    let r: unknown;
    svc.getRemittance('m1').subscribe(x => (r = x));
    ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances/m1')
      .flush({ remittance: { id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153,
        payDate: '2026-06-30', memo: null, status: 'Posted' } });
    expect(r).toEqual({ id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153,
      payDate: '2026-06-30', memo: null, status: 'Posted' });
    ctrl.verify();
  });

  it('fetches the posted entries for a source ref', () => {
    const { svc, ctrl } = setup();
    let entries: unknown;
    svc.entriesForSource('r1').subscribe(e => (entries = e));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries'
      && r.params.get('sourceRef') === 'r1');
    req.flush([{ id: 'e1', sequenceNumber: 5, effectiveDate: '2026-06-30', type: 'Standard', status: 'Open',
      posting: 'PendingApproval', lineCount: 5, supersedes: null, supersededBy: null, reversalOf: null,
      reversedBy: null, lines: [], sourceRef: 'r1', sourceType: 'PayrollRun', reference: null, memo: null, viaModule: 'payroll' }]);
    expect((entries as { id: string }[])[0].id).toBe('e1');
    ctrl.verify();
  });
});
