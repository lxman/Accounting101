import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EInvoiceImport } from './einvoice-import';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

const PREVIEW_URL = 'http://localhost:5000/clients/C1/einvoicing/inbound/preview';

export function xmlFile(name = 'invoice.xml'): File {
  return new File(['<xml/>'], name, { type: 'text/xml' });
}

export function blocked(ruleId = 'BR-06', message = 'Seller name is missing.') {
  return {
    syntax: 'Ubl', profile: 'urn:cen.eu:en16931:2017', canImport: false,
    findings: [{ ruleId, severity: 'Error', message, businessTerm: 'BT-27', location: null }],
    proposal: null, vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme Parts' },
  };
}

export function importable(overrides: Record<string, unknown> = {}) {
  return {
    syntax: 'Cii', profile: 'En16931', canImport: true, findings: [],
    proposal: {
      suggestedVendorId: null, sellerName: 'Acme Parts', billDate: '2026-07-01', dueDate: '2026-07-31',
      vendorReference: 'INV-9', memo: 'Q3 services', lines: [{ description: 'Consulting', amount: 1200 }],
      total: 1200, linesAreDetail: true, ...overrides,
    },
    vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme Parts' },
  };
}

export function boot() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
      provideHttpClientTesting(), provideCapabilities('ap.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(EInvoiceImport);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  // The screen loads vendors and accounts on construction (used from Task 6 onwards).
  ctrl.match(r => r.url.endsWith('/vendors')).forEach(r => r.flush([]));
  ctrl.match(r => r.url.endsWith('/accounts')).forEach(r => r.flush([]));
  fixture.detectChanges();
  return { fixture, ctrl, cmp: fixture.componentInstance };
}

describe('EInvoiceImport', () => {
  it('uploads the file and moves to the preview stage', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    const req = ctrl.expectOne(PREVIEW_URL);
    expect((req.request.body as FormData).get('file')).toBeInstanceOf(File);
    req.flush(blocked());
    fixture.detectChanges();
    expect(cmp.stage()).toBe('preview');
    ctrl.verify();
  });

  it('shows error findings verbatim and no form when the document cannot be imported', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(blocked());
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('BR-06');
    expect(text).toContain('Seller name is missing.');
    expect(text).toContain('EN 16931');                    // profile badge
    expect(text).toContain('Choose another file');
    expect(text).not.toContain('Create draft bill');
    ctrl.verify();
  });

  it('surfaces a 403 as a message and stays on the upload stage', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(
      { detail: 'The e-invoicing module is not enabled for this client.' },
      { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(cmp.stage()).toBe('upload');
    expect(cmp.message()).toContain('not enabled');
    ctrl.verify();
  });

  it('rejects an oversized file client-side without making a request', () => {
    const { ctrl, cmp } = boot();
    const big = new File(['x'], 'huge.pdf', { type: 'application/pdf' });
    Object.defineProperty(big, 'size', { value: 16 * 1024 * 1024 });
    cmp.file.set(big);
    cmp.upload();
    expect(cmp.message()).toContain('15 MB');
    ctrl.verify();                                          // no request was made
  });

  it('collapses advisory findings behind a count', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush({
      syntax: 'Cii', profile: 'En16931', canImport: false,
      findings: [
        { ruleId: 'BR-CO-10', severity: 'Warning', message: 'Sum of line amounts differs.', businessTerm: null, location: null },
        { ruleId: 'einvoicing-note', severity: 'Information', message: 'Detail lines reconciled.', businessTerm: null, location: null },
      ],
      proposal: null, vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme Parts' },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).not.toContain('BR-CO-10');
    cmp.showOtherFindings.set(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('BR-CO-10');
    expect(fixture.nativeElement.textContent).toContain('einvoicing-note');
    ctrl.verify();
  });

  it('prefills the vendor from the seller name once vendors have loaded', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
        provideHttpClientTesting(), provideCapabilities('ap.write')],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(EInvoiceImport);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.match(r => r.url.endsWith('/vendors')).forEach(r =>
      r.flush([{ id: 'v1', name: 'Acme Parts', email: null }]));
    ctrl.match(r => r.url.endsWith('/accounts')).forEach(r => r.flush([]));
    fixture.detectChanges();

    const cmp = fixture.componentInstance;
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('v1');
    ctrl.match(r => r.url.endsWith('/bills')).forEach(r => r.flush({ items: [], total: 0, skip: 0, limit: 200 }));
    ctrl.verify();
  });

  it('posts a draft bill carrying the document header and the edited lines', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    cmp.patchLine(0, { amount: 700, expenseAccountId: 'a1' });
    cmp.addLine();
    cmp.patchLine(1, { description: 'Utilities', amount: 500, expenseAccountId: 'a2' });
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));

    expect(cmp.canConfirm()).toBe(true);
    cmp.confirm();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills' && r.method === 'POST');
    expect(req.request.body).toEqual({
      vendorId: 'v1', billDate: '2026-07-01', dueDate: '2026-07-31',
      vendorReference: 'INV-9', memo: 'Q3 services',
      lines: [
        { description: 'Consulting', amount: 700, expenseAccountId: 'a1' },
        { description: 'Utilities', amount: 500, expenseAccountId: 'a2' },
      ],
    });
    req.flush({ id: 'b9', vendorId: 'v1', number: 'B-9', billDate: '2026-07-01', dueDate: '2026-07-31',
      vendorReference: 'INV-9', memo: 'Q3 services', status: 'Draft', lines: [] });
    fixture.detectChanges();
    expect(cmp.createdBillId()).toBe('b9');
    expect(fixture.nativeElement.textContent).toContain('Draft bill created');
    ctrl.verify();
  });

  it('warns on total drift without blocking the confirm', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    cmp.patchLine(0, { amount: 900, expenseAccountId: 'a1' });
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));
    fixture.detectChanges();

    expect(cmp.totalDrift()).toBe(-300);
    expect(fixture.nativeElement.textContent).toContain('document says');
    expect(cmp.canConfirm()).toBe(true);                    // a warning, not a gate
    ctrl.verify();
  });

  it('creates the vendor inline from the seller name and selects it', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('');

    cmp.createVendor();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendors' && r.method === 'POST');
    expect(req.request.body).toEqual({ name: 'Acme Parts', email: null });
    req.flush({ id: 'v7', name: 'Acme Parts', email: null });
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('v7');
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));
    ctrl.verify();
  });

  it('does not let a stale confirm() response clobber a document opened after reset()', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    cmp.patchLine(0, { amount: 1200, expenseAccountId: 'a1' });
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));

    cmp.confirm();
    const staleReq = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills' && r.method === 'POST');

    cmp.reset();                                              // operator abandons this document
    cmp.file.set(xmlFile('invoice2.xml'));
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable({ vendorReference: 'INV-2', memo: 'Second doc' }));
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));

    staleReq.flush({ id: 'STALE', vendorId: 'v1', number: 'B-1', billDate: '2026-07-01', dueDate: '2026-07-31',
      vendorReference: 'INV-9', memo: 'Q3 services', status: 'Draft', lines: [] });
    fixture.detectChanges();

    expect(cmp.createdBillId()).toBeNull();
    expect(cmp.proposal()?.vendorReference).toBe('INV-2');
    expect(fixture.nativeElement.textContent).not.toContain('Draft bill created');
    ctrl.verify();
  });

  it('does not let a stale createVendor() response clobber a document opened after reset()', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('');

    cmp.createVendor();
    const staleReq = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/vendors' && r.method === 'POST');

    cmp.reset();                                              // operator abandons this document
    cmp.file.set(xmlFile('invoice2.xml'));
    cmp.upload();
    // A different seller than the abandoned document's, so PayablesService.create()'s own tap()
    // adding the stale vendor to the shared list can't legitimately auto-match this one by name —
    // isolates the generation-guard fix from the (correct, separate) vendor-match effect.
    ctrl.expectOne(PREVIEW_URL).flush({
      syntax: 'Cii', profile: 'En16931', canImport: true, findings: [],
      proposal: {
        suggestedVendorId: null, sellerName: 'Other Co', billDate: '2026-08-01', dueDate: '2026-08-31',
        vendorReference: 'INV-2', memo: 'Second doc', lines: [{ description: 'Freight', amount: 400 }],
        total: 400, linesAreDetail: true,
      },
      vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Other Co' },
    });
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills') && r.method === 'GET').forEach(r =>
      r.flush({ items: [], total: 0, skip: 0, limit: 200 }));

    staleReq.flush({ id: 'v-stale', name: 'Acme Parts', email: null });
    fixture.detectChanges();

    expect(cmp.vendorId()).toBe('');
    expect(cmp.creatingVendor()).toBe(false);
    ctrl.verify();
  });

  it('warns when the vendor already has a bill with the same reference', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    const req = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills' && r.params.get('vendorId') === 'v1');
    expect(req.request.params.get('limit')).toBe('200');
    expect(req.request.params.get('order')).toBe('desc');
    req.flush({ items: [
      { bill: { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] },
        openBalance: 0, settlementStatus: 'Paid' },
    ], total: 1, skip: 0, limit: 200 });
    fixture.detectChanges();

    expect(cmp.duplicates().map(b => b.id)).toEqual(['b1']);
    expect(fixture.nativeElement.textContent).toContain('already has a bill');
    expect(cmp.canConfirm()).toBe(false);                    // no accounts coded yet, not because of the warning
    ctrl.verify();
  });

  it('says what it actually checked when the scan is bounded', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills')
      .flush({ items: [], total: 512, skip: 0, limit: 200 });
    fixture.detectChanges();

    expect(cmp.duplicates()).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('200 most recent');
    ctrl.verify();
  });

  it('does not check when the document carries no vendor reference', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable({ vendorReference: null }));
    fixture.detectChanges();
    cmp.vendorId.set('v1');
    fixture.detectChanges();
    expect(cmp.duplicateChecked()).toBe(false);
    ctrl.verify();                                            // no /bills request at all
  });
});
