import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { FiscalSettings } from './fiscal';

@Injectable({ providedIn: 'root' })
export class FiscalService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(): string {
    return `${environment.apiBaseUrl}/admin/clients/${this.client.clientId()}/fiscal-year-end`;
  }

  get(): Observable<FiscalSettings> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<FiscalSettings>(this.base());
  }

  set(month: number): Observable<FiscalSettings> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<FiscalSettings>(this.base(), { fiscalYearEndMonth: month });
  }
}
