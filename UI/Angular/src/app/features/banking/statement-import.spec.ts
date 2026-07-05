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
    ctrl.verify();
  });

  it('confirms a preview by posting a bank statement', () => {
    const { fixture, ctrl } = boot();
    const cmp = fixture.componentInstance;
    cmp.cashAccountId.set('CA1');
    cmp.previews.set([{ lines: [{ date: '2026-03-05', amount: 100, description: 'dep', externalRef: null }],
      openingBalance: 0, closingBalance: 100, statementDate: '2026-03-31' }]);
    cmp.stage.set('preview');
    cmp.confirm(0);
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/bank-statements');
    expect(req.request.body.closingBalance).toBe(100);
    req.flush({ id: 'b1', number: 'BST-00001', cashAccountId: 'CA1', statementDate: '2026-03-31',
      openingBalance: 0, closingBalance: 100, lines: [], status: 'Posted' });
    ctrl.verify();
  });
});
