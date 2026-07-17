import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { PeriodStatus, CloseResponse, CloseYearResponse } from './periods';

@Injectable({ providedIn: 'root' })
export class PeriodsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path: string): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  status(): Observable<PeriodStatus> {
    return this.http.get<PeriodStatus>(this.base('/periods/status'));
  }

  close(asOf: string): Observable<CloseResponse> {
    return this.http.post<CloseResponse>(this.base('/periods/close'), { asOf });
  }

  closeYear(fiscalYearEnd: string): Observable<CloseYearResponse> {
    return this.http.post<CloseYearResponse>(this.base('/periods/close-year'), { fiscalYearEnd });
  }
}
