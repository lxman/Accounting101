import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { BillDetail } from './bill-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

describe('BillDetail', () => {
  function setup(id = 'b1') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        provideCapabilities('ap.write'),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } } } },
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushLoads(ctrl: HttpTestingController, status: string, id = 'b1') {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush([
      { id: 'a1', number: '6100', name: 'Rent Expense', type: 'Expense', parentId: null, postable: true,
        requiredDimension: null, requiredDimensions: [], cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: true }]);
    ctrl.expectOne(`http://localhost:5000/clients/C1/bills/${id}`).flush({ bill: { id, vendorId: 'v1',
      number: status === 'Draft' ? null : 'B-1', billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9',
      memo: null, status, lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] },
      openBalance: 1200, settlementStatus: 'Open' });
  }

  it('renders a draft bill and enters it', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Draft');
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Rent Expense');
    f.componentInstance.enter();
    const req = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/b1/enter');
    // Entering promotes the draft to a brand-new evidentiary bill id; the enter response carries that id.
    req.flush({ id: 'b9', vendorId: 'v1', number: 'B-1', billDate: '2026-06-30', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] });
    // reload after enter hits the NEW entered id (b9), not the old draft id (b1) — the draft is gone.
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/b9').flush({ bill: { id: 'b9', vendorId: 'v1', number: 'B-1',
      billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null, status: 'Entered',
      lines: [{ description: 'Rent', amount: 1200, expenseAccountId: 'a1' }] }, openBalance: 1200, settlementStatus: 'Open' });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
    ctrl.verify();
  });

  it('enter promotes the draft and re-points to the entered id', async () => {
    const ctrl = setup('d1');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Draft', 'd1');
    f.detectChanges();
    f.componentInstance.enter();
    ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills/d1/enter')
      .flush({ id: 'e9', vendorId: 'v1', number: 'BILL-00001', billDate: '2026-03-01', dueDate: null,
        vendorReference: null, memo: null, status: 'Entered', lines: [] });
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'e9'], { replaceUrl: true });
    // The re-point triggers a reload against the NEW entered id (e9), then loads its payments.
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/e9').flush({ bill: { id: 'e9', vendorId: 'v1',
      number: 'BILL-00001', billDate: '2026-03-01', dueDate: null, vendorReference: null, memo: null,
      status: 'Entered', lines: [] }, openBalance: 0, settlementStatus: 'Paid' });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments').flush([]);
    ctrl.verify();
  });

  it('discard deletes the draft and returns to the list', async () => {
    const ctrl = setup('d1');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillDetail);
    f.detectChanges();
    flushLoads(ctrl, 'Draft', 'd1');
    f.detectChanges();
    f.componentInstance.deleteBill();
    const del = ctrl.expectOne(r => r.method === 'DELETE' && r.url === 'http://localhost:5000/clients/C1/bills/d1');
    expect(del.request.method).toBe('DELETE');
    del.flush(null, { status: 204, statusText: 'No Content' });
    expect(nav).toHaveBeenCalledWith(['/payables']);
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
