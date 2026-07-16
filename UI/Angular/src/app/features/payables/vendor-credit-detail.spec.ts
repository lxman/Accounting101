import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { VendorCreditDetail } from './vendor-credit-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(VendorCreditDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('VendorCreditDetail', () => {
  it('renders header, allocations with total, and journal link', () => {
    const { fixture, ctrl } = boot('ca1');
    ctrl.expectOne('http://localhost:5000/clients/C1/vendor-credit-applications/ca1').flush({
      credit: { id: 'ca1', vendorId: 'v1', date: '2026-06-30', allocations: [], voided: false },
      allocations: [
        { billId: 'b1', billNumber: 'BILL-00001', amount: 60 },
        { billId: 'b2', billNumber: 'BILL-00002', amount: 40 },
      ],
      journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Credit applied');
    expect(text).toContain('BILL-00001');
    expect(text).toContain('60.00');
    expect(text).toContain('BILL-00002');
    expect(text).toContain('40.00');
    expect(text).toContain('100.00');   // allocations total = the amount
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('ca2');
    ctrl.expectOne('http://localhost:5000/clients/C1/vendor-credit-applications/ca2').flush({
      credit: { id: 'ca2', vendorId: 'v1', date: '2026-06-30', allocations: [], voided: false },
      allocations: [{ billId: 'b1', billNumber: 'BILL-00001', amount: 25 }], journalEntryId: null,
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('ca3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/vendor-credit-applications/ca3').flush({
      credit: { id: 'ca3', vendorId: 'v1', date: '2026-06-30', allocations: [], voided: false },
      allocations: [{ billId: 'b1', billNumber: 'BILL-00001', amount: 30 }], journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
