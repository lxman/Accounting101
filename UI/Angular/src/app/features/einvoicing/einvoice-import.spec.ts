import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
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
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));
    ctrl.verify();
  });

  it('matches the vendor via the constructor effect when vendors load after the preview', () => {
    // Unlike the test above, /vendors is deliberately left unflushed until after the preview
    // lands, so seed()'s own matchVendor() call sees an empty vendor list and cannot match —
    // this exercises the constructor's vendor-match effect, not seed().
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
        provideHttpClientTesting(), provideCapabilities('ap.write')],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(EInvoiceImport);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    const cmp = fixture.componentInstance;

    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('');                          // no vendors loaded yet — seed() alone can't match

    ctrl.match(r => r.url.endsWith('/vendors')).forEach(r =>
      r.flush([{ id: 'v1', name: 'Acme Parts', email: null }]));
    ctrl.match(r => r.url.endsWith('/accounts')).forEach(r => r.flush([]));
    fixture.detectChanges();
    expect(cmp.vendorId()).toBe('v1');                        // the effect fills it once vendors arrive

    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));
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
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));

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
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));
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
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));
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
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));

    cmp.confirm();
    const staleReq = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/bills' && r.method === 'POST');

    cmp.reset();                                              // operator abandons this document
    cmp.file.set(xmlFile('invoice2.xml'));
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable({ vendorReference: 'INV-2', memo: 'Second doc' }));
    fixture.detectChanges();
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));

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
    ctrl.match(r => r.url.endsWith('/bills/duplicates')).forEach(r => r.flush([]));

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
      r.url === 'http://localhost:5000/clients/C1/bills/duplicates'
      && r.params.get('vendorId') === 'v1'
      && r.params.get('reference') === 'INV-9');
    req.flush([{ id: 'b1', vendorId: 'v1', number: 'BILL-00001', billDate: '2026-03-01', dueDate: null,
      vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] }]);
    fixture.detectChanges();

    expect(cmp.duplicates().map(b => b.id)).toEqual(['b1']);
    expect(fixture.nativeElement.textContent).toContain('already has a bill');
    ctrl.verify();
  });

  it('warns about a duplicate that is still a draft', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/bills/duplicates')).flush([
      { id: 'b1', vendorId: 'v1', number: null, billDate: '2026-03-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Draft', lines: [] },
    ]);
    fixture.detectChanges();

    expect(cmp.duplicates().map(b => b.id)).toEqual(['b1']);
    expect(fixture.nativeElement.textContent).toContain('Draft');
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
    expect(cmp.duplicates()).toEqual([]);
    ctrl.verify();                                            // no duplicates request at all
  });

  it('does not check when the vendor reference is whitespace-only', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable({ vendorReference: '   ' }));
    fixture.detectChanges();
    cmp.vendorId.set('v1');
    fixture.detectChanges();
    expect(cmp.duplicates()).toEqual([]);
    ctrl.verify();                                            // no duplicates request at all
  });

  it('a duplicate never blocks the confirm', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    cmp.patchLine(0, { expenseAccountId: 'a1' });             // fully coded, so nothing else gates it
    fixture.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/bills/duplicates')).flush([
      { id: 'b1', vendorId: 'v1', number: 'BILL-00001', billDate: '2026-03-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] },
    ]);
    fixture.detectChanges();

    expect(cmp.duplicates().length).toBe(1);
    expect(cmp.canConfirm()).toBe(true);                      // a warning, not a gate
    ctrl.verify();
  });

  it('clears a stale duplicate strip when a vendor change is followed by a failed re-scan', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');
    fixture.detectChanges();
    const firstReq = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills/duplicates' && r.params.get('vendorId') === 'v1');
    firstReq.flush([
      { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] },
    ]);
    fixture.detectChanges();
    expect(cmp.duplicates().map(b => b.id)).toEqual(['b1']);
    expect(fixture.nativeElement.textContent).toContain('already has a bill');

    // Operator realises the seller is a different vendor (Globex, not Acme) and switches.
    cmp.vendorId.set('v2');
    fixture.detectChanges();
    // The stale strip — and its live link to Acme's bill — must be gone immediately, before the
    // new scan even resolves, not just once the new scan happens to succeed.
    expect(cmp.duplicates()).toEqual([]);
    expect(fixture.nativeElement.textContent).not.toContain('already has a bill');

    const secondReq = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills/duplicates' && r.params.get('vendorId') === 'v2');
    secondReq.flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(cmp.duplicates()).toEqual([]);
    expect(fixture.nativeElement.textContent).not.toContain('already has a bill');
    ctrl.verify();
  });

  it("does not let a superseded vendor's slow scan overwrite a later vendor's clean result", () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();

    cmp.vendorId.set('v1');                                   // slow scan, will resolve last
    fixture.detectChanges();
    const slowReq = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills/duplicates' && r.params.get('vendorId') === 'v1');

    cmp.vendorId.set('v2');                                   // fast scan, clean, resolves first
    fixture.detectChanges();
    const fastReq = ctrl.expectOne(r =>
      r.url === 'http://localhost:5000/clients/C1/bills/duplicates' && r.params.get('vendorId') === 'v2');
    fastReq.flush([]);
    fixture.detectChanges();
    expect(cmp.duplicates()).toEqual([]);

    // Vendor A's slow response lands last — it must not overwrite vendor B's already-clean result.
    slowReq.flush([
      { id: 'b1', vendorId: 'v1', number: 'B-1', billDate: '2026-06-01', dueDate: null,
        vendorReference: 'INV-9', memo: null, status: 'Entered', lines: [] },
    ]);
    fixture.detectChanges();

    expect(cmp.duplicates()).toEqual([]);
    expect(fixture.nativeElement.textContent).not.toContain('already has a bill');
    ctrl.verify();
  });

  it('creates a blob URL for a PDF upload and revokes it on reset', () => {
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const { fixture, cmp } = boot();

    const pdf = new File(['%PDF-1.7'], 'invoice.pdf', { type: 'application/pdf' });
    cmp.onFile({ target: { files: [pdf] } } as unknown as Event);
    fixture.detectChanges();
    expect(create).toHaveBeenCalledWith(pdf);
    expect(cmp.pdfUrl()).toBe('blob:fake');

    cmp.reset();
    expect(revoke).toHaveBeenCalledWith('blob:fake');
    expect(cmp.pdfUrl()).toBeNull();
    create.mockRestore();
    revoke.mockRestore();
  });

  it('creates no blob URL for an XML upload', () => {
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:fake');
    const { cmp } = boot();
    cmp.onFile({ target: { files: [xmlFile()] } } as unknown as Event);
    expect(create).not.toHaveBeenCalled();
    expect(cmp.pdfUrl()).toBeNull();
    create.mockRestore();
  });

  it('resets to the upload stage when the active client changes', () => {
    const { fixture, ctrl, cmp } = boot();
    cmp.file.set(xmlFile());
    cmp.upload();
    ctrl.expectOne(PREVIEW_URL).flush(importable());
    fixture.detectChanges();
    expect(cmp.stage()).toBe('preview');

    TestBed.inject(ClientContextService).select('C2');
    fixture.detectChanges();
    expect(cmp.stage()).toBe('upload');
    expect(cmp.preview()).toBeNull();
    ctrl.match(() => true).forEach(r => r.flush([]));        // vendors/accounts refetch for C2
    ctrl.verify();
  });
});
