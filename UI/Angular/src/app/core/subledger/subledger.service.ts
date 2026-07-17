import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { SubledgerReconciliationsResponse, SubledgerResponse } from './subledger';

@Injectable({ providedIn: 'root' })
export class SubledgerService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path: string): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  reconciliations(): Observable<SubledgerReconciliationsResponse> {
    return this.http.get<SubledgerReconciliationsResponse>(this.base('/subledger/reconciliations'));
  }

  breakdown(account: string, dimension: string): Observable<SubledgerResponse> {
    return this.http.get<SubledgerResponse>(this.base(`/subledger?account=${account}&dimension=${dimension}`));
  }
}
