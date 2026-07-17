import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
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
  aging: { current: 100, d1To30: 200, d31To60: 300, d61To90: 400, d90Plus: 900 },
  openInvoices: [{ invoiceId: 'i1', number: '1001', issueDate: '2026-03-01', dueDate: '2026-03-31', openBalance: 400, daysOverdue: 45 }],
  statementLines: [{ date: '2026-03-01', type: 'Invoice', reference: '1001', charge: 1000, payment: 0, balance: 1000, id: 'i1', kind: 'invoice' }],
  creditLines: [{ date: '2026-03-18', type: 'Overpayment', reference: null, amount: 100, creditBalance: 100, id: 'p1', kind: 'payment' }],
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
    // Wire-contract guard: d1To30 bucket value must render (catches future casing regressions)
    expect(text).toContain('200.00');        // aging d1To30 = 200
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

  it('drills each statement and credit-activity row into its document detail', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(CustomerAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/account').flush({
      customer: { id: 'cu1', name: 'Acme Co', email: null }, arBalance: 0, creditBalance: 0,
      aging: { current: 0, d1To30: 0, d31To60: 0, d61To90: 0, d90Plus: 0 },
      openInvoices: [],
      statementLines: [
        { date: '2026-03-01', type: 'Invoice', reference: '1001', charge: 1000, payment: 0, balance: 1000, id: 'i1', kind: 'invoice' },
        { date: '2026-03-02', type: 'Payment', reference: null, charge: 0, payment: 100, balance: 900, id: 'p1', kind: 'payment' },
        { date: '2026-03-03', type: 'Credit note', reference: null, charge: 0, payment: 50, balance: 850, id: 'n1', kind: 'credit-note' },
        { date: '2026-03-04', type: 'Write-off', reference: null, charge: 0, payment: 25, balance: 825, id: 'w1', kind: 'write-off' },
        { date: '2026-03-05', type: 'Credit applied', reference: null, charge: 0, payment: 10, balance: 815, id: 'c1', kind: 'credit-application' },
      ],
      creditLines: [
        { date: '2026-03-06', type: 'Overpayment', reference: null, amount: 40, creditBalance: 40, id: 'p2', kind: 'payment' },
        { date: '2026-03-07', type: 'Refund', reference: null, amount: -20, creditBalance: 20, id: 'r1', kind: 'refund' },
      ],
    });
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const rows = [...f.nativeElement.querySelectorAll('tbody tr')] as HTMLElement[];   // openInvoices empty → 5 statement + 2 credit
    rows.forEach(r => r.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    expect(nav.mock.calls.map(c => c[0])).toEqual([
      ['/receivables/invoices', 'i1'],
      ['/receivables/payments', 'p1'],
      ['/receivables/credits', 'credit-note', 'n1'],
      ['/receivables/credits', 'write-off', 'w1'],
      ['/receivables/credits', 'credit-application', 'c1'],
      ['/receivables/payments', 'p2'],
      ['/receivables/refunds', 'r1'],
    ]);
  });
});
