import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorAccount } from './vendor-account';
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
  vendor: { id: 'v1', name: 'Acme Parts', email: 'acme@x.com' }, apBalance: 800, creditBalance: 200,
  aging: { current: 0, d1To30: 0, d31To60: 800, d61To90: 0, d90Plus: 0 },
  openBills: [{ billId: 'b2', number: 'B-2', billDate: '2026-02-01', dueDate: '2026-02-15', openBalance: 800, daysOverdue: 59 }],
  statementLines: [{ date: '2026-02-01', type: 'Bill', reference: 'B-2', charge: 800, payment: 0, balance: 800, id: 'b2', kind: 'bill' }],
  creditLines: [{ date: '2026-03-15', type: 'Overpayment', reference: null, amount: 200, creditBalance: 200, id: 'p1', kind: 'payment' }],
});

describe('VendorAccount', () => {
  it('renders header, aging, open bills, statement, and credit activity', () => {
    const ctrl = setup('v1');
    const f = TestBed.createComponent(VendorAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/account').flush(view());
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('Acme Parts');
    expect(text).toContain('acme@x.com');
    expect(text).toContain('800.00');         // AP balance + open bill + statement
    expect(text).toContain('B-2');            // open bill + statement ref
    expect(text).toContain('Overpayment');    // credit activity
    expect(text).toContain('200.00');         // credit balance + d31To60 bucket guard value
  });

  it('relays a not-found error', () => {
    const ctrl = setup('nope');
    const f = TestBed.createComponent(VendorAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/nope/account').flush(
      { type: 'about:blank', title: 'Not Found', detail: 'Vendor not found.', status: 404 },
      { status: 404, statusText: 'Not Found' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Vendor not found.');
  });

  it('drills each statement and credit-activity row into its document detail', () => {
    const ctrl = setup('v1');
    const f = TestBed.createComponent(VendorAccount); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/vendors/v1/account').flush({
      vendor: { id: 'v1', name: 'Acme Parts', email: null }, apBalance: 0, creditBalance: 0,
      aging: { current: 0, d1To30: 0, d31To60: 0, d61To90: 0, d90Plus: 0 },
      openBills: [],
      statementLines: [
        { date: '2026-03-01', type: 'Bill', reference: 'BILL-00001', charge: 1000, payment: 0, balance: 1000, id: 'b1', kind: 'bill' },
        { date: '2026-03-02', type: 'Payment', reference: null, charge: 0, payment: 100, balance: 900, id: 'p1', kind: 'payment' },
        { date: '2026-03-03', type: 'Credit applied', reference: null, charge: 0, payment: 60, balance: 840, id: 'c1', kind: 'credit-application' },
      ],
      creditLines: [
        { date: '2026-03-04', type: 'Overpayment', reference: null, amount: 40, creditBalance: 40, id: 'p2', kind: 'payment' },
        { date: '2026-03-05', type: 'Credit applied', reference: null, amount: -10, creditBalance: 30, id: 'c2', kind: 'credit-application' },
      ],
    });
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const rows = [...f.nativeElement.querySelectorAll('tbody tr')] as HTMLElement[];   // openBills empty → 3 statement + 2 credit
    rows.forEach(r => r.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    expect(nav.mock.calls.map(c => c[0])).toEqual([
      ['/payables/bills', 'b1'],
      ['/payables/payments', 'p1'],
      ['/payables/credits', 'c1'],
      ['/payables/payments', 'p2'],
      ['/payables/credits', 'c2'],
    ]);
  });
});
