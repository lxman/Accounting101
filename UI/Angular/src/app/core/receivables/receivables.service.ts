import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, tap } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { Customer, DraftInvoiceRequest, Invoice, InvoiceListQuery, InvoiceView } from './receivables';
import { extractProblem } from '../api/problem-details';

@Injectable({ providedIn: 'root' })
export class ReceivablesService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly _customers = signal<Customer[]>([]);
  readonly customers = this._customers.asReadonly();
  private readonly byId = computed(() => new Map(this._customers().map(c => [c.id, c])));
  private readonly _loadError = signal<string | null>(null);
  readonly loadError = this._loadError.asReadonly();

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  load(): void {
    const id = this.client.clientId(); if (!id) return;
    this.http.get<Customer[]>(this.base('/customers')).subscribe({
      next: cs => { this._customers.set(cs); this._loadError.set(null); },
      error: e => this._loadError.set(extractProblem(e).detail),
    });
  }
  create(name: string, email?: string | null): Observable<Customer> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<Customer>(this.base('/customers'), { name, email: email ?? null })
      .pipe(tap(c => this._customers.update(list => [...list, c])));
  }
  customerName(id: string): string { return this.byId().get(id)?.name ?? id; }

  listInvoices(q: InvoiceListQuery): Observable<PagedResponse<InvoiceView>> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    let params = new HttpParams().set('customerId', q.customerId).set('skip', q.skip).set('limit', q.limit);
    if (q.settlement) params = params.set('settlement', q.settlement);
    if (q.order) params = params.set('order', q.order);
    return this.http.get<PagedResponse<InvoiceView>>(this.base('/invoices'), { params });
  }
  getInvoice(id: string): Observable<InvoiceView> { return this.http.get<InvoiceView>(this.base(`/invoices/${id}`)); }
  draft(req: DraftInvoiceRequest): Observable<Invoice> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<Invoice>(this.base('/invoices'), req);
  }
  updateDraft(id: string, req: DraftInvoiceRequest): Observable<Invoice> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.put<Invoice>(this.base(`/invoices/${id}`), req);
  }
  deleteDraft(id: string): Observable<void> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.delete<void>(this.base(`/invoices/${id}`));
  }
  issue(id: string): Observable<Invoice> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<Invoice>(this.base(`/invoices/${id}/issue`), {});
  }
  void(id: string, reason?: string | null): Observable<Invoice> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<Invoice>(this.base(`/invoices/${id}/void`), { reason: reason ?? null });
  }
}
