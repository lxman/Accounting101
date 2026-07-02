import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunDetail } from './run-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(id = 'r1') {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities('payroll.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } },
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

function flushLoad(ctrl: HttpTestingController, status: string, id = 'r1') {
  ctrl.expectOne(`http://localhost:5000/clients/C1/payroll-runs/${id}`).flush({ run: { id, number: 'PR-1',
    gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50, incomeTaxWithheld: 120,
    payDate: '2026-06-30', memo: null, status } });
  ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries' && r.params.get('sourceRef') === id)
    .flush([{ id: 'e1', sequenceNumber: 5, effectiveDate: '2026-06-30', type: 'Standard', status: 'Open',
      posting: 'PendingApproval', lineCount: 5, supersedes: null, supersededBy: null, reversalOf: null,
      reversedBy: null, lines: [], sourceRef: id, sourceType: 'PayrollRun', reference: null, memo: null, viaModule: 'payroll' }]);
}

describe('RunDetail', () => {
  it('renders the run, net pay, and a link to the posted entry', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('PR-1');
    expect(f.nativeElement.textContent).toContain('753.50');       // net pay
    const link = f.nativeElement.querySelector('a[href="/journal/e1"]');
    expect(link).toBeTruthy();
    ctrl.verify();
  });

  it('voids a posted run with a reason', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    f.componentInstance.reason.set('entered twice');
    f.componentInstance.void();
    const del = ctrl.expectOne('http://localhost:5000/clients/C1/payroll-runs/r1/void');
    expect(del.request.body).toEqual({ reason: 'entered twice' });
    del.flush({ id: 'r1', number: 'PR-1', gross: 1000, employeeFica: 76.5, employerFica: 76.5, deductions: 50,
      incomeTaxWithheld: 120, payDate: '2026-06-30', memo: null, status: 'Void' });
    flushLoad(ctrl, 'Void');       // reload after void
    f.detectChanges();
    ctrl.verify();
  });
});
