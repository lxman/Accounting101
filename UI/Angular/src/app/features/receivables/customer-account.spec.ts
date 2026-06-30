import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CustomerAccount } from './customer-account';
import { ClientContextService } from '../../core/client/client-context.service';

function setup(id: string) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: (k: string) => (k === 'id' ? id : null) } } } },
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const view = () => ({
  customer: { id: 'cu1', name: 'Acme Co', email: 'acme@x.com' }, arBalance: 1900, creditBalance: 50,
  aging: { current: 100, d1to30: 200, d31to60: 300, d61to90: 400, d90plus: 900 },
  openInvoices: [{ invoiceId: 'i1', number: '1001', issueDate: '2026-03-01', dueDate: '2026-03-31', openBalance: 400, daysOverdue: 45 }],
  statementLines: [{ date: '2026-03-01', type: 'Invoice', reference: '1001', charge: 1000, payment: 0, balance: 1000 }],
  creditLines: [{ date: '2026-03-18', type: 'Overpayment', reference: null, amount: 100, creditBalance: 100 }],
});

describe('CustomerAccount', () => {
  it('renders header, aging, open invoices, statement, and credit activity', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(CustomerAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/account').flush(view());
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('Acme Co');
    expect(text).toContain('acme@x.com');
    expect(text).toContain('1,900.00');     // AR balance
    expect(text).toContain('1001');          // open invoice + statement ref
    expect(text).toContain('Overpayment');   // credit activity
  });

  it('relays a not-found error', () => {
    const ctrl = setup('nope');
    const f = TestBed.createComponent(CustomerAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/nope/account').flush(
      { type: 'about:blank', title: 'Not Found', detail: 'Customer not found.', status: 404 },
      { status: 404, statusText: 'Not Found' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Customer not found.');
  });
});
