import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CreditDetail } from './credit-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot(type: string, id: string, caps: string[] = ['gl.read']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['type', type], ['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(CreditDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('CreditDetail', () => {
  it('renders header, allocations table with total, and journal link', () => {
    const { fixture, ctrl } = boot('credit-note', 'cn1');
    ctrl.expectOne('http://localhost:5000/clients/C1/credits/credit-note/cn1').flush({
      credit: { type: 'credit-note', id: 'cn1', customerId: 'cu1', date: '2026-06-30', amount: 100, memo: 'returned goods', voided: false },
      allocations: [
        { invoiceId: 'inv1', invoiceNumber: '1042', amount: 60 },
        { invoiceId: 'inv2', invoiceNumber: '1051', amount: 40 },
      ],
      journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Credit note');
    expect(text).toContain('returned goods');
    expect(text).toContain('1042');
    expect(text).toContain('60.00');
    expect(text).toContain('1051');
    expect(text).toContain('40.00');
    expect(text).toContain('100.00');   // total
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('renders a credit-application with a dash memo and no journal link when null', () => {
    const { fixture, ctrl } = boot('credit-application', 'ca1');
    ctrl.expectOne('http://localhost:5000/clients/C1/credits/credit-application/ca1').flush({
      credit: { type: 'credit-application', id: 'ca1', customerId: 'cu1', date: '2026-06-30', amount: 50, memo: null, voided: false },
      allocations: [{ invoiceId: 'inv4', invoiceNumber: '1099', amount: 50 }],
      journalEntryId: null,
    });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Apply credit');
    expect(text).toContain('—');   // memo dash
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });

  it('hides the journal link when the user lacks gl.read', () => {
    const { fixture, ctrl } = boot('credit-note', 'cn3', []);
    ctrl.expectOne('http://localhost:5000/clients/C1/credits/credit-note/cn3').flush({
      credit: { type: 'credit-note', id: 'cn3', customerId: 'cu1', date: '2026-06-30', amount: 20, memo: 'x', voided: false },
      allocations: [{ invoiceId: 'inv1', invoiceNumber: '1042', amount: 20 }],
      journalEntryId: 'e9',
    });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
