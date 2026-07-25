import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EInvoicingService } from './einvoicing.service';
import { ClientContextService } from '../client/client-context.service';

describe('EInvoicingService', () => {
  function setup(clientId: string | null) {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select(clientId);
    return { svc: TestBed.inject(EInvoicingService), ctrl: TestBed.inject(HttpTestingController) };
  }

  it('posts the file as multipart to the preview endpoint', () => {
    const { svc, ctrl } = setup('C1');
    const file = new File(['<xml/>'], 'invoice.xml', { type: 'text/xml' });
    let got: unknown = null;
    svc.previewInbound(file).subscribe(r => (got = r));

    const req = ctrl.expectOne('http://localhost:5000/clients/C1/einvoicing/inbound/preview');
    expect(req.request.method).toBe('POST');
    const body = req.request.body as FormData;
    expect(body.get('file')).toBeInstanceOf(File);
    expect((body.get('file') as File).name).toBe('invoice.xml');

    req.flush({ syntax: 'Ubl', profile: null, canImport: false, findings: [], proposal: null,
      vendorMatch: { vendorId: null, vendorName: null, sellerName: 'Acme' } });
    expect(got).not.toBeNull();
    ctrl.verify();
  });

  it('makes no request when no client is selected', () => {
    const { svc, ctrl } = setup(null);
    let emitted = false;
    svc.previewInbound(new File(['x'], 'a.xml')).subscribe({ next: () => (emitted = true) });
    expect(emitted).toBe(false);
    ctrl.verify();
  });

  it('mirrors the endpoint 15 MB cap', () => {
    expect(EInvoicingService.maxUploadBytes).toBe(15 * 1024 * 1024);
  });
});
