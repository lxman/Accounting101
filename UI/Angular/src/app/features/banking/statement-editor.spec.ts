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

  it('scans for duplicates once the account and date are both set, and renders the hits', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    const req = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(req.request.params.get('cashAccountId')).toBe('CA1');
    expect(req.request.params.get('statementDate')).toBe(cmp.statementDate());
    req.flush([{ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates().length).toBe(1);
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    ctrl.verify();
  });

  it('never disables Record statement because of a duplicate', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    cmp.patch(0, { date: '2026-03-05', description: 'dep', amount: 100 });
    cmp.closingBalance.set(100);
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates().length).toBe(1);
    expect(cmp.canSave()).toBe(true);
    ctrl.verify();
  });

  it('ignores a response superseded by a later scan', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    const first = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));
    cmp.setStatementDate('2026-04-30');
    const second = ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30');
    second.flush([]);
    first.flush([{ id: 'stale', number: 'BST-09999', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates()).toEqual([]);
    ctrl.verify();
  });

  it('leaves no stale hit on screen when a re-scan fails', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.setCashAccount('CA1');
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: cmp.statementDate(),
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.duplicates().length).toBe(1);
    cmp.setStatementDate('2026-04-30');
    ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30')
        .flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(cmp.duplicates()).toEqual([]);
    ctrl.verify();
  });
});
