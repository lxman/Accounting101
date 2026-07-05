import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementList } from './statement-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

describe('StatementList', () => {
  it('loads statements once a cash account is selected', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('bankrec.write')],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(StatementList);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
      [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
    fixture.componentInstance.cashAccountId.set('CA1');
    fixture.detectChanges();
    const req = ctrl.expectOne(r => r.url.endsWith('/bank-statements'));
    expect(req.request.params.get('cashAccountId')).toBe('CA1');
    req.flush({ items: [{ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }], total: 1, skip: 0, limit: 50 });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    ctrl.verify();
  });
});
