import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RemittanceDetail } from './remittance-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(id = 'm1') {
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

function flushLoad(ctrl: HttpTestingController, status: string, id = 'm1') {
  ctrl.expectOne(`http://localhost:5000/clients/C1/tax-remittances/${id}`).flush({ remittance: { id, number: 'TR-1',
    withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: null, status } });
  ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries' && r.params.get('sourceRef') === id)
    .flush([{ id: 'e2', sequenceNumber: 6, effectiveDate: '2026-06-30', type: 'Standard', status: 'Open',
      posting: 'PendingApproval', lineCount: 3, supersedes: null, supersededBy: null, reversalOf: null,
      reversedBy: null, lines: [], sourceRef: id, sourceType: 'TaxRemittance', reference: null, memo: null, viaModule: 'payroll' }]);
}

describe('RemittanceDetail', () => {
  it('renders the remittance, total, and posted-entry link', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RemittanceDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('TR-1');
    expect(f.nativeElement.textContent).toContain('323.00');
    expect(f.nativeElement.querySelector('a[href="/journal/e2"]')).toBeTruthy();
    ctrl.verify();
  });

  it('voids a posted remittance', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RemittanceDetail); f.detectChanges();
    flushLoad(ctrl, 'Posted');
    f.detectChanges();
    f.componentInstance.void();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances/m1/void');
    expect(req.request.body).toEqual({ reason: null });
    req.flush({ id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: null, status: 'Void' });
    flushLoad(ctrl, 'Void');
    f.detectChanges();
    ctrl.verify();
  });
});
