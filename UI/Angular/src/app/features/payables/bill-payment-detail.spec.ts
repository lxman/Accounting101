import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BillPaymentDetail } from './bill-payment-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(BillPaymentDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('BillPaymentDetail', () => {
  it('renders header, method, allocations with total, unapplied line, and journal link', () => {
    const { fixture, ctrl } = boot('p1');
    ctrl.expectOne('http://localhost:5000/clients/C1/bill-payments/p1').flush({
      payment: { id: 'p1', vendorId: 'v1', date: '2026-06-30', amount: 150, method: 'check', allocations: [], voided: false },
      allocations: [
        { billId: 'b1', billNumber: 'BILL-00001', amount: 100 },
        { billId: 'b2', billNumber: 'BILL-00002', amount: 30 },
      ],
      unapplied: 20, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('check');
    expect(text).toContain('BILL-00001');
    expect(text).toContain('100.00');
    expect(text).toContain('BILL-00002');
    expect(text).toContain('30.00');
    expect(text).toContain('130.00');   // allocations total
    expect(text).toContain('20.00');    // unapplied
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('p2');
    ctrl.expectOne('http://localhost:5000/clients/C1/bill-payments/p2').flush({
      payment: { id: 'p2', vendorId: 'v1', date: '2026-06-30', amount: 25, method: null, allocations: [], voided: false },
      allocations: [], unapplied: 25, journalEntryId: null,
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('p3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/bill-payments/p3').flush({
      payment: { id: 'p3', vendorId: 'v1', date: '2026-06-30', amount: 30, method: 'cash', allocations: [], voided: false },
      allocations: [{ billId: 'b1', billNumber: 'BILL-00001', amount: 30 }], unapplied: 0, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
