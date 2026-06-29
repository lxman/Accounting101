import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { TrialBalanceResponse } from './trial-balance';

@Injectable({ providedIn: 'root' })
export class TrialBalanceService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  get(asOf?: string) {
    const id = this.client.clientId();
    let params = new HttpParams();
    if (asOf) params = params.set('asOf', asOf);
    return this.http.get<TrialBalanceResponse>(
      `${environment.apiBaseUrl}/clients/${id}/trial-balance`,
      { params },
    );
  }
}
