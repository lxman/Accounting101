import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillPaymentEditor } from './bill-payment-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { vi } from 'vitest';

describe('BillPaymentEditor', () => {
  function setup(vendor = 'v1') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        provideCapabilities('ap.write'),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => k === 'vendor' ? vendor : null } } } },
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushInit(ctrl: HttpTestingController) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills').flush({
      items: [{ bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-05-01', dueDate: null,
        vendorReference: null, memo: null, status: 'Entered',
        lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] }, openBalance: 100, settlementStatus: 'Open' }],
      total: 1, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 0 });
  }

  it('auto-allocates oldest-first when the amount changes', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillPaymentEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.onAmount(60);
    expect(cmp.rows()[0].allocation).toBe(60);
    expect(cmp.unallocated()).toBe(0);
    ctrl.verify();
  });

  it('records a payment with allocations and navigates to the payments list', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillPaymentEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.onAmount(100);
    cmp.save();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bill-payments');
    expect(post.request.body).toEqual({ vendorId: 'v1', date: cmp.date(), amount: 100, method: null,
      allocations: [{ targetId: 'b1', amount: 100 }] });
    post.flush({ id: 'p9', vendorId: 'v1', date: cmp.date(), amount: 100, method: null,
      allocations: [{ targetId: 'b1', amount: 100 }], voided: false });
    expect(nav).toHaveBeenCalledWith(['/payables/payments']);
    ctrl.verify();
  });

  it('warns and disables Save when allocations exceed the payment amount', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillPaymentEditor); f.detectChanges();
    flushInit(ctrl); f.detectChanges();                       // bill b1 open 100
    const cmp = f.componentInstance;
    cmp.onAmount(50); cmp.onRow(0, 80); f.detectChanges();    // allocated 80 > amount 50 (row 80 <= open 100)
    expect(cmp.valid()).toBe(false);
    expect(cmp.allocationWarning()).toContain('exceeds the payment amount');
    expect(f.nativeElement.textContent).toContain('exceeds the payment amount');
    cmp.onRow(0, 40); f.detectChanges();
    expect(cmp.valid()).toBe(true);
    expect(cmp.allocationWarning()).toBeNull();
    ctrl.verify();
  });
});
