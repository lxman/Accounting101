import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PaymentList } from './payment-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('ar.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const payment = (id: string, amount: number, allocated: number) => ({
  id, customerId: 'cu1', date: '2026-06-30', amount, method: 'check',
  allocations: [{ targetId: 'inv1', amount: allocated }], voided: false,
});

describe('PaymentList', () => {
  it('loads the selected customer\'s payments and renders amount/method/allocated/unapplied', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(PaymentList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments') && r.params.get('customerId') === 'cu1')
      .flush([payment('p1', 100, 60)]);
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('100.00');   // amount
    expect(text).toContain('check');    // method
    expect(text).toContain('60.00');    // allocated
    expect(text).toContain('40.00');    // unapplied = 100 - 60
  });

  it('Record payment link targets the editor for the selected customer; disabled with none', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(PaymentList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const link = () => [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record payment') as HTMLAnchorElement;
    expect(link().className).toContain('opacity-50');               // disabled-styled, no customer
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/payments') && r.params.get('customerId') === 'cu1').flush([]);
    f.detectChanges();
    expect(link().getAttribute('href')).toContain('/receivables/payments/new');
    expect(link().getAttribute('href')).toContain('customer=cu1');
  });

  it('shows the empty states', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(PaymentList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No customers yet');
  });
});
