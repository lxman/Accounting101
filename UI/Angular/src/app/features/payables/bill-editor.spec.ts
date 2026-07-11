import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillEditor } from './bill-editor';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { vi } from 'vitest';

describe('BillEditor', () => {
  function setup(editId?: string) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        provideCapabilities('ap.write'),
        ...(editId ? [{ provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => editId } } } }] : []),
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
    const ctrl = TestBed.inject(HttpTestingController);
    return ctrl;
  }

  function flushRefData(ctrl: HttpTestingController) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush([
      { id: 'a1', number: '6100', name: 'Rent Expense', type: 'Expense', parentId: null, postable: true,
        requiredDimension: null, requiredDimensions: [], cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: true },
      { id: 'a2', number: '6200', name: 'Utilities Expense', type: 'Expense', parentId: null, postable: false,
        requiredDimension: null, requiredDimensions: [], cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: true },
      { id: 'a3', number: '6300', name: 'Meals Expense', type: 'Expense', parentId: null, postable: true,
        requiredDimension: null, requiredDimensions: [], cashFlowActivity: null, isRetainedEarnings: false, active: false,
        normalSide: 'Debit', isTemporary: true },
      { id: 'c1', number: '1000', name: 'Cash', type: 'Asset', parentId: null, postable: true,
        requiredDimension: null, requiredDimensions: [], cashFlowActivity: null, isRetainedEarnings: false, active: true,
        normalSide: 'Debit', isTemporary: false },
    ]);
  }

  it('shows only postable active expense accounts in the line picker', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillEditor);
    f.detectChanges();
    flushRefData(ctrl);
    f.detectChanges();
    expect(f.componentInstance.expenseAccounts().map(a => a.id)).toEqual(['a1']);
    ctrl.verify();
  });

  it('posts a draft bill with a line and navigates to its detail', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillEditor);
    f.detectChanges();
    flushRefData(ctrl);
    f.detectChanges();

    const cmp = f.componentInstance;
    cmp.model.update(v => ({ ...v, vendorId: 'v1', billDate: '2026-06-30',
      lines: [{ lineId: 'L1', description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] }));
    f.detectChanges();
    expect(cmp.canSave()).toBe(true);
    cmp.save();

    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/bills');
    expect(post.request.body).toEqual({ vendorId: 'v1', billDate: '2026-06-30', dueDate: null,
      vendorReference: null, memo: null, lines: [{ description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] });
    post.flush({ id: 'b9', vendorId: 'v1', number: null, billDate: '2026-06-30', dueDate: null,
      vendorReference: null, memo: null, status: 'Draft', lines: post.request.body.lines });
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'b9']);
    ctrl.verify();
  });

  it('edit mode loads the draft and PUTs on save', async () => {
    const ctrl = setup('d1');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillEditor);
    f.detectChanges();
    flushRefData(ctrl);                                  // vendors + accounts
    f.detectChanges();                                    // let the effect observe vendors loaded → calls getBill
    // prefill effect loads the existing draft via GET.
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/d1').flush({ bill: { id: 'd1', vendorId: 'v1',
      number: null, billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null,
      status: 'Draft', lines: [{ description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] },
      openBalance: 1200, settlementStatus: 'Open' });
    f.detectChanges();

    expect(f.componentInstance.editId).toBe('d1');
    expect(f.componentInstance.model().vendorId).toBe('v1');
    expect(f.componentInstance.model().lines.length).toBe(1);
    expect(f.componentInstance.canSave()).toBe(true);

    f.componentInstance.save();
    const put = ctrl.expectOne(r => r.method === 'PUT' && r.url === 'http://localhost:5000/clients/C1/bills/d1');
    expect(put.request.body).toEqual({ vendorId: 'v1', billDate: '2026-06-30', dueDate: null,
      vendorReference: 'INV-9', memo: null,
      lines: [{ description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] });
    put.flush({ id: 'd1', vendorId: 'v1', number: null, billDate: '2026-06-30', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Draft',
      lines: [{ description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] });
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'd1']);
    ctrl.verify();
  });

  it('edit mode discards the draft and returns to the list', async () => {
    const ctrl = setup('d1');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillEditor);
    f.detectChanges();
    flushRefData(ctrl);                                  // vendors + accounts
    f.detectChanges();                                    // effect observes vendors loaded → prefill GET
    ctrl.expectOne('http://localhost:5000/clients/C1/bills/d1').flush({ bill: { id: 'd1', vendorId: 'v1',
      number: null, billDate: '2026-06-30', dueDate: null, vendorReference: 'INV-9', memo: null,
      status: 'Draft', lines: [{ description: 'June rent', amount: 1200, expenseAccountId: 'a1' }] },
      openBalance: 1200, settlementStatus: 'Open' });
    f.detectChanges();

    f.componentInstance.discard();
    const del = ctrl.expectOne(r => r.method === 'DELETE' && r.url === 'http://localhost:5000/clients/C1/bills/d1');
    del.flush(null, { status: 204, statusText: 'No Content' });
    expect(nav).toHaveBeenCalledWith(['/payables']);
    ctrl.verify();
  });
});
