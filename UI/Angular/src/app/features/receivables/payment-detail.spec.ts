import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PaymentDetail } from './payment-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(PaymentDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('PaymentDetail', () => {
  it('renders header, method, allocations with total, unapplied line, and journal link', () => {
    const { fixture, ctrl } = boot('p1');
    ctrl.expectOne('http://localhost:5000/clients/C1/payments/p1').flush({
      payment: { id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 150, method: 'check', allocations: [], voided: false },
      allocations: [
        { invoiceId: 'inv1', invoiceNumber: '1042', amount: 60 },
        { invoiceId: 'inv2', invoiceNumber: '1051', amount: 40 },
      ],
      unapplied: 50, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('check');
    expect(text).toContain('1042');
    expect(text).toContain('60.00');
    expect(text).toContain('1051');
    expect(text).toContain('40.00');
    expect(text).toContain('100.00');   // allocations total
    expect(text).toContain('50.00');    // unapplied
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('p2');
    ctrl.expectOne('http://localhost:5000/clients/C1/payments/p2').flush({
      payment: { id: 'p2', customerId: 'cu1', date: '2026-06-30', amount: 25, method: null, allocations: [], voided: false },
      allocations: [], unapplied: 25, journalEntryId: null,
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('p3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/payments/p3').flush({
      payment: { id: 'p3', customerId: 'cu1', date: '2026-06-30', amount: 30, method: 'cash', allocations: [], voided: false },
      allocations: [{ invoiceId: 'inv1', invoiceNumber: '1042', amount: 30 }], unapplied: 0, journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
