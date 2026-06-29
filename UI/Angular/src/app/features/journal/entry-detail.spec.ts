import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { EntryDetail } from './entry-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function provideRouteId(id: string) {
  return { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => id } }, paramMap: of({ get: () => id }) } };
}

describe('EntryDetail', () => {
  let ctrl: HttpTestingController;
  function setup(id = 'E1') {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideRouteId(id)],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  }
  const seedAccounts = [
    { id: 'A', number: '1000', name: 'Cash', type: 'Asset', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false },
    { id: 'B', number: '4000', name: 'Revenue', type: 'Revenue', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Credit', isTemporary: true },
  ];
  function flushEntryAndAudit(authorSub: string) {
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush(seedAccounts);
    const e = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1');
    e.flush({ id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2,
      lines: [{ accountId: 'A', direction: 'Debit', amount: 100, dimensions: {}, lineMemo: null }, { accountId: 'B', direction: 'Credit', amount: 100, dimensions: {}, lineMemo: null }],
      supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null, memo: 'm', reference: 'r', sourceRef: null, sourceType: null });
    const a = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1');
    a.flush([{ sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '2026-06-29T00:00:00Z', reason: null, actor: { userId: authorSub, name: 'Author', claims: [] } }]);
  }

  afterEach(() => ctrl.verify());

  it('renders lines and the creator stamp', () => {
    setup(); const f = TestBed.createComponent(EntryDetail); f.detectChanges();
    flushEntryAndAudit(environment.devApprover.sub); f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Author');
    expect(f.componentInstance.entry()?.lines.length).toBe(2);
  });

  it('approve() surfaces a 403 SoD inline', () => {
    setup(); const f = TestBed.createComponent(EntryDetail); f.detectChanges();
    flushEntryAndAudit(environment.devClerk.sub); f.detectChanges(); // active = clerk = author
    f.componentInstance.approve();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1/approve');
    req.flush({ detail: 'Segregation of duties: must be approved by someone else.' }, { status: 403, statusText: 'Forbidden' });
    f.detectChanges();
    expect(f.componentInstance.message()).toContain('Segregation of duties');
  });

  it('renders source line when sourceRef is set', () => {
    setup(); const f = TestBed.createComponent(EntryDetail); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush(seedAccounts);
    ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1').flush({
      id: 'E1', sequenceNumber: 2, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'Posted', lineCount: 2,
      lines: [{ accountId: 'A', direction: 'Debit', amount: 50, dimensions: {}, lineMemo: null }, { accountId: 'B', direction: 'Credit', amount: 50, dimensions: {}, lineMemo: null }],
      supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null, memo: null, reference: null, sourceRef: 'INV-001', sourceType: 'Invoice',
    });
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Invoice');
    expect(f.nativeElement.textContent).toContain('INV-001');
  });

  it('void() posts the reason and re-fetches', () => {
    setup(); const f = TestBed.createComponent(EntryDetail); f.detectChanges();
    flushEntryAndAudit(environment.devApprover.sub); f.detectChanges();
    f.componentInstance.voidReason.set('mistake');
    f.componentInstance.voidEntry();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1/void');
    expect(req.request.body).toEqual({ reason: 'mistake' });
    req.flush({ id: 'E1', posting: 'Posted', status: 'Voided', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', lineCount: 2, lines: [], supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null });
    // re-fetch
    ctrl.expectOne('http://localhost:5000/clients/C1/entries/E1').flush({ id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Voided', posting: 'Posted', lineCount: 0, lines: [], supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null });
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush([]);
  });
});
