import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorCreditList } from './vendor-credit-list';
import { PayablesService } from '../../core/payables/payables.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

describe('VendorCreditList', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('ap.write')],
    });
    TestBed.inject(ClientContextService).select('C1');
    return TestBed.inject(HttpTestingController);
  }

  it('prompts to select a vendor when none is selected', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(VendorCreditList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Select a vendor');
    ctrl.verify();
  });

  it('shows the credit balance and lists applications for the selected vendor', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(VendorCreditList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 50 });
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications' && r.params.get('vendorId') === 'v1');
    req.flush([{ id: 'ca1', vendorId: 'v1', date: '2026-04-02', allocations: [{ targetId: 'b2', amount: 40 }], voided: false }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Available credit');
    expect(f.nativeElement.textContent).toContain('40');
    ctrl.verify();
  });

  it('navigates to the vendor-credit detail when a row is clicked', () => {
    const ctrl = setup();
    const svc = TestBed.inject(PayablesService);
    const f = TestBed.createComponent(VendorCreditList);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors').flush([{ id: 'v1', name: 'Acme Parts', email: null }]);
    svc.setSelectedVendor('v1');
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/credit-balance').flush({ vendorId: 'v1', creditBalance: 50 });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendor-credit-applications' && r.params.get('vendorId') === 'v1')
      .flush([{ id: 'ca1', vendorId: 'v1', date: '2026-04-02', allocations: [{ targetId: 'b2', amount: 40 }], voided: false }]);
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(nav).toHaveBeenCalledWith(['/payables/credits', 'ca1']);
  });
});
