import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillDetail } from './bill-detail';
import { ClientContextService } from '../../core/client/client-context.service';

describe('BillDetail', () => {
  function setup(id = 'b1') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } },
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushLoads(ctrl: HttpTestingController, status: string) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush([
      { id: 'a1', number: '6100', name: 'Rent Expense', type: 'Expense', parentId: null, postable: true,
        requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: true }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1',
      number: status === 'Draft' ? null : 'B-1', billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9',
      memo: null, status, lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] },
      openBalance: 1200, settlementStatus: 'Open' });
  }

  it('renders a draft bill and enters it', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Draft');
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Rent Expense');
    f.componentInstance.enter();
    const req = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/enter');
    req.flush({ id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-30', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] });
    // reload after enter
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1', number: 'B-1',
      billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null, status: 'Entered',
      lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] }, openBalance: 1200, settlementStatus: 'Open' });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
    ctrl.verify();
  });

  it('voids an entered bill with a reason', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Entered');
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.voidReason.set('duplicate');
    cmp.voidBill();
    const req = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/void');
    expect(req.request.body).toEqual({ reason: 'duplicate' });
    req.flush({ id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-30', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Void', lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] });
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1', number: 'B-1',
      billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null, status: 'Void',
      lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] }, openBalance: 0, settlementStatus: 'Open' });
    ctrl.verify();
  });

  it('shows applied payments on an entered bill and voids one', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Entered');
    // Entered bills load the vendor's payments for the applied-payments section.
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments' && r.params.get('vendorId') === 'v1')
      .flush([{ id: 'p1', vendorId: 'v1', date: '2026-06-10', amount: 1200, method: 'check',
        allocations: [{ targetId: 'b1', amount: 1200 }], voided: false }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Applied payments');

    f.componentInstance.voidPayment({ id: 'p1', vendorId: 'v1', date: '2026-06-10', amount: 1200, method: 'check',
      allocations: [{ targetId: 'b1', amount: 1200 }], voided: false } as any);
    const v = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bill-payments/p1/void');
    v.flush({});
    // reload: bill + payments fetched again
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b1').flush({ bill: { id: 'b1', vendorId: 'v1', number: 'B-1',
      billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null, status: 'Entered',
      lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] }, openBalance: 1200, settlementStatus: 'Open' });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
    ctrl.verify();
  });
});
