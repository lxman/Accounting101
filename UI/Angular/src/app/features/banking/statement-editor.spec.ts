import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementEditor } from './statement-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('bankrec.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(StatementEditor);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('StatementEditor', () => {
  it('blocks save until the statement foots, then posts', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1'); cmp.openingBalance.set(0); cmp.closingBalance.set(100);
    cmp.setLine(0, { date: '2026-03-05', amount: 60, description: 'dep', externalRef: null });
    expect(cmp.foots()).toBe(false);           // 0 + 60 ≠ 100
    cmp.addLine();
    cmp.setLine(1, { date: '2026-03-06', amount: 40, description: 'dep2', externalRef: null });
    expect(cmp.foots()).toBe(true);            // 0 + 100 = 100
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements');
    expect(req.request.body.lines.length).toBe(2);
    req.flush({ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' });
    ctrl.verify();
  });
});
