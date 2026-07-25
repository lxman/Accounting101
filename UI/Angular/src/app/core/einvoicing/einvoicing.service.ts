import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { InboundPreviewResponse } from './einvoicing';

/**
 * The e-invoicing module's whole HTTP surface: one advisory, stateless preview call. The module
 * writes nothing — turning a preview into a bill is the screen's job, through Payables' own API.
 */
@Injectable({ providedIn: 'root' })
export class EInvoicingService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  /** Mirrors `EInvoicingEndpoints.MaxUploadBytes`. Checked client-side because the endpoint's own
   *  friendly 413 is unreachable for an oversized body: `WithFormOptions` caps the multipart
   *  reader, so Kestrel rejects mid-stream with a bare 400 before the handler ever runs. */
  static readonly maxUploadBytes = 15 * 1024 * 1024;

  /** 200-with-findings is the success contract — a 200 can carry `canImport: false`. */
  previewInbound(file: File): Observable<InboundPreviewResponse> {
    const id = this.client.clientId();
    if (!id) return EMPTY;
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http.post<InboundPreviewResponse>(
      `${environment.apiBaseUrl}/clients/${id}/einvoicing/inbound/preview`, body);
  }
}
