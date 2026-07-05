import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AdjustmentsPanel } from './adjustments-panel';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('bankrec.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(AdjustmentsPanel);
  fixture.componentRef.setInput('reconciliationId', 'r1');
  fixture.componentRef.setInput('locked', false);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'o1', number: '6900', name: 'Bank fees', type: 'Expense', postable: true }]);   // bare array — accounts endpoint is not paged
  ctrl.expectOne(r => r.url.endsWith('/reconciliations/r1/adjustments')).flush({ items: [], total: 0, skip: 0, limit: 50 });
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('AdjustmentsPanel', () => {
  it('records a charge and emits changed', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    let emitted = false; cmp.changed.subscribe(() => (emitted = true));
    cmp.offsetAccountId.set('o1'); cmp.amount.set(12.5); cmp.kind.set('Charge');
    cmp.record();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/reconciliations/r1/adjustments');
    expect(req.request.body.amount).toBe(12.5);
    req.flush({ id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'CA1', offsetAccountId: 'o1',
      kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: null, status: 'Posted' });
    // list reloads
    ctrl.expectOne(r => r.url.endsWith('/reconciliations/r1/adjustments')).flush({ items: [
      { id: 'j1', number: 'ADJ-00001', reconciliationId: 'r1', cashAccountId: 'CA1', offsetAccountId: 'o1',
        kind: 'Charge', amount: 12.5, date: '2026-03-31', memo: null, status: 'Posted' }], total: 1, skip: 0, limit: 50 });
    expect(emitted).toBe(true);
    ctrl.verify();
  });
});
