import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReconciliationWorksheet } from './reconciliation-worksheet';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

const worksheet = (over: Record<string, unknown> = {}) => ({
  reconciliation: { id: 'r1', number: 'REC-00001', cashAccountId: 'CA1', bankStatementId: 'b1',
    statementDate: '2026-03-31', status: 'InProgress', clearedEntryIds: [] },
  statement: { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
    openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' },
  entries: [{ entryId: 'e1', date: '2026-03-05', reference: null, sourceType: 'Cash', cashEffect: 100, cleared: false }],
  bookBalance: 100, clearedTotal: 0, reconciledDifference: 100, balanced: false, ...over });

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities('bankrec.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'r1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(ReconciliationWorksheet);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/reconciliations/r1')).flush(worksheet());
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('ReconciliationWorksheet', () => {
  it('clears an entry and reflects the balanced verdict', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.toggle({ entryId: 'e1', date: '2026-03-05', reference: null, sourceType: 'Cash', cashEffect: 100, cleared: false });
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/clear');
    expect(req.request.body).toEqual({ entryIds: ['e1'] });
    req.flush(worksheet({ clearedTotal: 100, reconciledDifference: 0, balanced: true,
      entries: [{ entryId: 'e1', date: '2026-03-05', reference: null, sourceType: 'Cash', cashEffect: 100, cleared: true }] }));
    fixture.detectChanges();
    expect(cmp.worksheet()!.balanced).toBe(true);
    ctrl.verify();
  });
});
