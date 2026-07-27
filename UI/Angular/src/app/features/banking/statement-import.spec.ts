import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { StatementImport } from './statement-import';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('bankrec.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(StatementImport);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'CA1', number: '1000', name: 'Cash', type: 'Asset', postable: true }]);   // bare array — accounts endpoint is not paged
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('StatementImport', () => {
  it('uploads a CSV with a built mapping and moves to preview', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.file.set(new File(['x'], 'bank.csv', { type: 'text/csv' }));
    cmp.format.set('Csv');
    cmp.setColumn('date', 0); cmp.setColumn('amount', 1); cmp.setColumn('description', 2);
    cmp.upload();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import');
    const mapping = JSON.parse((req.request.body as FormData).get('mapping') as string);
    expect(mapping.date.index).toBe(0);
    req.flush({ statements: [{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
      detectedOpeningBalance: 0, detectedClosingBalance: 100, statementDate: '2026-03-31', accountHint: null }], warnings: [] });
    fixture.detectChanges();
    expect(cmp.stage()).toBe('preview');
    expect(cmp.previews().length).toBe(1);
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([]);
    ctrl.verify();
  });

  it('confirms a preview by posting a bank statement', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
      openingBalance: 0, closingBalance: 100, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');
    cmp.confirm(0);
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements');
    expect(req.request.body.closingBalance).toBe(100);
    req.flush({ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' });
    ctrl.verify();
  });

  it('scans each preview for duplicates on entering the preview stage', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.file.set(new File(['x'], 'bank.csv', { type: 'text/csv' }));
    cmp.setColumn('date', 0); cmp.setColumn('amount', 1); cmp.setColumn('description', 2);
    cmp.upload();
    ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import').flush({
      statements: [
        { lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
          detectedOpeningBalance: 0, detectedClosingBalance: 100, statementDate: '2026-03-31', accountHint: null },
        { lines: [{ date: '2026-04-05', amount: 50, description: 'dep', externalRef: null }],
          detectedOpeningBalance: 0, detectedClosingBalance: 50, statementDate: '2026-04-30', accountHint: null },
      ], warnings: [],
    });
    fixture.detectChanges();

    const scans = ctrl.match(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(scans.length).toBe(2);
    expect(scans.map(s => s.request.params.get('statementDate'))).toEqual(['2026-03-31', '2026-04-30']);
    scans[0].flush([{ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    scans[1].flush([]);
    fixture.detectChanges();

    expect(cmp.previews()[0].duplicates.length).toBe(1);
    expect(cmp.previews()[1].duplicates).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('BST-00001');
    ctrl.verify();
  });

  it('re-scans only the edited preview when its date changes', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([
      { lines: [], openingBalance: 0, closingBalance: 0, statementDate: '2026-03-31', duplicates: [] },
      { lines: [], openingBalance: 0, closingBalance: 0, statementDate: '2026-04-30', duplicates: [] },
    ]);
    cmp.stage.set('preview');
    cmp.setStatementDate(0, '2026-05-31');
    const reqs = ctrl.match(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(reqs.length).toBe(1);
    expect(reqs[0].request.params.get('statementDate')).toBe('2026-05-31');
    reqs[0].flush([]);
    expect(cmp.previews()[0].statementDate).toBe('2026-05-31');
    expect(cmp.previews()[1].statementDate).toBe('2026-04-30');
    ctrl.verify();
  });

  it('ignores a scan response for a date the preview has moved off', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [], openingBalance: 0, closingBalance: 0, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');
    cmp.setStatementDate(0, '2026-04-30');
    const first = ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30');
    cmp.setStatementDate(0, '2026-05-31');
    ctrl.expectOne(r => r.params.get('statementDate') === '2026-05-31').flush([]);
    first.flush([{ id: 'stale', number: 'BST-09999', cashAccountId: 'CA1', statementDate: '2026-04-30',
      openingBalance: 0, closingBalance: 0, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates).toEqual([]);
    ctrl.verify();
  });

  it('never disables Record statement because of a duplicate', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
      openingBalance: 0, closingBalance: 100, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');
    cmp.setStatementDate(0, '2026-03-31');
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
        openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates.length).toBe(1);
    expect(cmp.foots(cmp.previews()[0])).toBe(true);
    const btn = Array.from(fixture.nativeElement.querySelectorAll('button'))
      .find((b) => (b as HTMLButtonElement).textContent?.trim() === 'Record statement') as HTMLButtonElement | undefined;
    expect(btn).toBeTruthy();
    expect(btn!.disabled).toBe(false);
    ctrl.verify();
  });

  it('scans a defaulted date exactly like a detected one', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    const today = new Date().toISOString().slice(0, 10);
    cmp.cashAccountId.set('CA1');
    cmp.file.set(new File(['x'], 'bank.csv', { type: 'text/csv' }));
    cmp.setColumn('date', 0); cmp.setColumn('amount', 1); cmp.setColumn('description', 2);
    cmp.upload();
    ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import').flush({
      statements: [{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
        detectedOpeningBalance: 0, detectedClosingBalance: 100, statementDate: null, accountHint: null }],
      warnings: [],
    });
    fixture.detectChanges();

    const scan = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));
    expect(scan.request.params.get('statementDate')).toBe(today);
    scan.flush([]);
    ctrl.verify();
  });

  // A stale ERROR must not wipe out a newer scan that already succeeded. Note what this test does NOT
  // do: flush a failing scan and assert duplicates is empty. That assertion holds whether or not the
  // error handler is guarded, because scan() clears duplicates synchronously before any response
  // arrives — it is a test that cannot fail. The guard only earns its place in the ordering below.
  it('keeps a newer scan\'s hits when an older scan errors late', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [], openingBalance: 0, closingBalance: 0, statementDate: '2026-03-31', duplicates: [] }]);
    cmp.stage.set('preview');

    cmp.setStatementDate(0, '2026-04-30');
    const older = ctrl.expectOne(r => r.params.get('statementDate') === '2026-04-30');   // left in flight

    cmp.setStatementDate(0, '2026-05-31');
    ctrl.expectOne(r => r.params.get('statementDate') === '2026-05-31').flush([
      { id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-05-31',
        openingBalance: 0, closingBalance: 0, lines: [], status: 'Posted' }]);
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates.length).toBe(1);

    older.flush('boom', { status: 500, statusText: 'Server Error' });                    // arrives late
    fixture.detectChanges();
    expect(cmp.previews()[0].duplicates.length).toBe(1);   // the newer scan's hit survives
    expect(cmp.message()).toBeNull();                      // a failed scan is not a failed import
    ctrl.verify();
  });

  // The `key` guard alone explains every test above: in each one the preview array survives and only
  // the date moves. This proves the OTHER half of the design — reset() discards the whole preview set
  // while a scan is in flight, then a new import lands a preview at the same index whose statementDate
  // is the SAME as the discarded one (2026-03-31 both times). `key` cannot see any difference there;
  // only the generation bump in reset() can tell the stale response apart from the current one.
  it('discards a stale scan after reset, even when the new preview has the same date', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.file.set(new File(['x'], 'bank.csv', { type: 'text/csv' }));
    cmp.setColumn('date', 0); cmp.setColumn('amount', 1); cmp.setColumn('description', 2);
    cmp.upload();
    ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import').flush({
      statements: [{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
        detectedOpeningBalance: 0, detectedClosingBalance: 100, statementDate: '2026-03-31', accountHint: null }],
      warnings: [],
    });
    fixture.detectChanges();
    const stale = ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates'));   // left in flight across reset()

    cmp.reset();

    cmp.file.set(new File(['y'], 'bank2.csv', { type: 'text/csv' }));
    cmp.upload();
    ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements/import').flush({
      statements: [{ lines: [{ date: '2026-04-05', amount: 50, description: 'dep', externalRef: null }],
        detectedOpeningBalance: 0, detectedClosingBalance: 50, statementDate: '2026-03-31', accountHint: null }],   // same date as the discarded preview
      warnings: [],
    });
    fixture.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/bank-statements/duplicates')).flush([]);
    fixture.detectChanges();

    stale.flush([{ id: 'stale', number: 'BST-09999', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' }]);
    fixture.detectChanges();

    expect(cmp.previews()[0].duplicates).toEqual([]);   // the discarded generation's hit never lands
    ctrl.verify();
  });
});
