import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillPaymentList } from './bill-payment-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';

describe('BillPaymentList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  it('prompts to select a vendor when none is selected', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(BillPaymentList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Select a vendor');
    ctrl.verify();
  });

  it('lists the selected vendor\'s payments', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(BillPaymentList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
    f.detectChanges();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bill-payments' && r.params.get('vendorId') === 'v1');
    req.flush([{ id: 'p1', vendorId: 'v1', date: '2026-06-01', amount: 100, method: 'check',
      allocations: [{ targetId: 'b1', amount: 80 }], voided: false }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('check');
    ctrl.verify();
  });
});
