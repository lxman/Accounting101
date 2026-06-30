import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PayablesService } from './payables.service';
import { ClientContextService } from '../client/client-context.service';

describe('PayablesService', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return { svc: TestBed.inject(PayablesService), ctrl: TestBed.inject(HttpTestingController) };
  }

  it('loads vendors into the signal', () => {
    const { svc, ctrl } = setup();
    svc.load();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors')
      .flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    expect(svc.vendors().length).toBe(1);
    expect(svc.vendorName('v1')).toBe('Acme Parts');
    ctrl.verify();
  });

  it('lists bills for a vendor with settlement filter', () => {
    const { svc, ctrl } = setup();
    svc.listBills({ vendorId: 'v1', settlement: 'open', skip: 0, limit: 50 }).subscribe();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills');
    expect(req.request.params.get('vendorId')).toBe('v1');
    expect(req.request.params.get('settlement')).toBe('open');
    expect(req.request.params.get('skip')).toBe('0');
    expect(req.request.params.get('limit')).toBe('50');
    req.flush({ items: [], total: 0, skip: 0, limit: 50 });
    ctrl.verify();
  });

  it('posts a draft bill', () => {
    const { svc, ctrl } = setup();
    const body = { vendorId: 'v1', billDate: '2026-06-30', dueDate: null, vendorReference: null, memo: null,
      lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] };
    svc.draftBill(body).subscribe();
    const req = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills');
    expect(req.request.body).toEqual(body);
    req.flush({ id: 'b1', vendorId: 'v1', number: null, billDate: '2026-06-30', dueDate: null,
      vendorReference: null, memo: null, status: 'Draft', lines: body.lines });
    ctrl.verify();
  });

  it('enters and voids a bill', () => {
    const { svc, ctrl } = setup();
    svc.enter('b1').subscribe();
    ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/enter').flush({});
    svc.void('b1', 'oops').subscribe();
    const v = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/void');
    expect(v.request.body).toEqual({ reason: 'oops' });
    v.flush({});
    ctrl.verify();
  });

  it('gets a bill by id', () => {
    const { svc, ctrl } = setup();
    svc.getBill('b1').subscribe();
    ctrl.expectOne(r => r.method === 'GET' && r.url === 'http://localhost:5000/clients/C1/bills/b1').flush(
      { bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-30', dueDate: null,
        vendorReference: null, memo: null, status: 'Entered',
        lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] }, openBalance: 100, settlementStatus: 'Open' });
    ctrl.verify();
  });
});
