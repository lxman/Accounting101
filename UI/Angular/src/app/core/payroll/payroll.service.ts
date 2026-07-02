import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, map } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse } from '../entries/entry';
import {
  PayrollRun, TaxRemittance, PayrollRunView, TaxRemittanceView,
  RecordPayrollRunRequest, RecordTaxRemittanceRequest, PayrollListQuery,
} from './payroll';

@Injectable({ providedIn: 'root' })
export class PayrollService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  private listParams(q: PayrollListQuery): HttpParams {
    let p = new HttpParams().set('skip', q.skip).set('limit', q.limit);
    if (q.order) p = p.set('order', q.order);
    if (q.includeVoided) p = p.set('includeVoided', true);
    return p;
  }

  listRuns(q: PayrollListQuery): Observable<PagedResponse<PayrollRun>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<PayrollRunView>>(this.base('/payroll-runs'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.run) })));
  }

  getRun(id: string): Observable<PayrollRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PayrollRunView>(this.base(`/payroll-runs/${id}`)).pipe(map(v => v.run));
  }

  recordRun(req: RecordPayrollRunRequest): Observable<PayrollRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<PayrollRun>(this.base('/payroll-runs'), req);
  }

  voidRun(id: string, reason?: string | null): Observable<PayrollRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<PayrollRun>(this.base(`/payroll-runs/${id}/void`), { reason: reason ?? null });
  }

  listRemittances(q: PayrollListQuery): Observable<PagedResponse<TaxRemittance>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<TaxRemittanceView>>(this.base('/tax-remittances'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.remittance) })));
  }

  getRemittance(id: string): Observable<TaxRemittance> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<TaxRemittanceView>(this.base(`/tax-remittances/${id}`)).pipe(map(v => v.remittance));
  }

  recordRemittance(req: RecordTaxRemittanceRequest): Observable<TaxRemittance> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<TaxRemittance>(this.base('/tax-remittances'), req);
  }

  voidRemittance(id: string, reason?: string | null): Observable<TaxRemittance> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<TaxRemittance>(this.base(`/tax-remittances/${id}/void`), { reason: reason ?? null });
  }

  /** Posted journal entry(ies) for a payroll document — powers the "posted journal entry" link. */
  entriesForSource(sourceRef: string): Observable<EntryResponse[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<EntryResponse[]>(this.base('/entries'), { params: new HttpParams().set('sourceRef', sourceRef) });
  }
}
