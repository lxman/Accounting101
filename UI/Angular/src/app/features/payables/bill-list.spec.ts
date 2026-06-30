import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillList } from './bill-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { vi } from 'vitest';

describe('BillList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  function flushVendorsThenSelect(ctrl: HttpTestingController, svc: PayablesService) {
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
  }

  it('prompts to select a vendor when none is selected', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Select a vendor');
    ctrl.verify();
  });

  it('lists bills for the selected vendor', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(BillList);
    f.detectChanges();
    flushVendorsThenSelect(ctrl, svc);
    f.detectChanges();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills' && r.params.get('vendorId') === 'v1');
    req.flush({ items: [{ bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Entered',
      lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] }, openBalance: 100, settlementStatus: 'Open' }],
      total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('B-1');
    expect(f.nativeElement.textContent).toContain('INV-9');
    ctrl.verify();
  });

  it('clicking a bill row navigates to its detail', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(BillList);
    f.detectChanges();
    flushVendorsThenSelect(ctrl, svc);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills').flush({ items: [{ bill: { id: 'b1',
      vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null, vendorReference: null, memo: null,
      status: 'Entered', lines: [{ description: 'Rent', amount: 100, expenseAccountId: 'a1' }] },
      openBalance: 100, settlementStatus: 'Open' }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    (f.nativeElement.querySelector('tbody tr') as HTMLElement).click();
    expect(nav).toHaveBeenCalledWith(['/payables/bills', 'b1']);
    ctrl.verify();
  });
});
