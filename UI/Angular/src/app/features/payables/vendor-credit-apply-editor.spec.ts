import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorCreditApplyEditor } from './vendor-credit-apply-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('VendorCreditApplyEditor', () => {
  function setup(vendor = 'v1') {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => k === 'vendor' ? vendor : null } } } },
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushInit(ctrl: HttpTestingController) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 50 });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills').flush({
      items: [{ bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-05-01', dueDate: null,
        vendorReference: null, memo: null, status: 'Entered',
        lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] }, openBalance: 100, settlementStatus: 'Open' }],
      total: 1, skip: 0, limit: 200 });
  }

  it('auto-allocates the available credit oldest-first on load', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(VendorCreditApplyEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.available()).toBe(50);
    expect(cmp.rows()[0].allocation).toBe(50); // 50 credit, bill open 100 → 50 applied
    expect(cmp.allocated()).toBe(50);
    expect(cmp.valid()).toBe(true);
    ctrl.verify();
  });

  it('applies the credit and navigates to the credits list', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(VendorCreditApplyEditor);
    f.detectChanges();
    flushInit(ctrl);
    f.detectChanges();
    const cmp = f.componentInstance;
    cmp.save();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications');
    expect(post.request.body).toEqual({ vendorId: 'v1', date: cmp.date(), allocations: [{ targetId: 'b1', amount: 50 }] });
    post.flush({ id: 'ca9', vendorId: 'v1', date: cmp.date(), allocations: [{ targetId: 'b1', amount: 50 }], voided: false });
    expect(nav).toHaveBeenCalledWith(['/payables/credits']);
    ctrl.verify();
  });
});
