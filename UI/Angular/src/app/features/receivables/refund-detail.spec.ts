import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RefundDetail } from './refund-detail';
import { ClientContextService } from '../../core/client/client-context.service';

function boot(id: string) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', id]]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(RefundDetail);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  return { fixture, ctrl };
}

describe('RefundDetail', () => {
  it('renders refund fields and links to the journal entry', () => {
    const { fixture, ctrl } = boot('rf1');
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1').flush(
      { refund: { id: 'rf1', customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'overpayment', voided: false }, journalEntryId: 'e9' });
    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('50.00');
    expect(text).toContain('overpayment');
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry')) as HTMLAnchorElement;
    expect(link.getAttribute('href')).toContain('/journal/e9');
  });

  it('omits the journal link when journalEntryId is null', () => {
    const { fixture, ctrl } = boot('rf2');
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf2').flush(
      { refund: { id: 'rf2', customerId: 'cu1', date: '2026-06-30', amount: 25, memo: null, voided: false }, journalEntryId: null });
    fixture.detectChanges();
    const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')]
      .find(a => a.textContent!.includes('View journal entry'));
    expect(link).toBeUndefined();
  });
});
