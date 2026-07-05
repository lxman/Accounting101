import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ReconciliationList } from './reconciliation-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

describe('ReconciliationList', () => {
  it('starts a reconciliation from a chosen statement', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        provideCapabilities('bankrec.write'),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map() } } }],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(ReconciliationList);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
      [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    fixture.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/bank-statements')).flush({ items: [
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }], total: 1, skip: 0, limit: 50 });
    fixture.detectChanges();
    cmp.start('b1');
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations');
    expect(req.request.body).toEqual({ bankStatementId: 'b1' });
    req.flush({ id: 'r1', number: 'REC-00001', cashAccountId: 'CA1', bankStatementId: 'b1',
      statementDate: '2026-03-31', status: 'InProgress', clearedEntryIds: [] });
    ctrl.verify();
  });
});
