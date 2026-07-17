import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PostingAccounts, ChartAccount } from './posting-accounts';

@Injectable({ providedIn: 'root' })
export class PostingAccountsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private clientBase(): string {
    return `${environment.apiBaseUrl}/clients/${this.client.clientId()}`;
  }

  get(): Observable<PostingAccounts> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PostingAccounts>(`${this.clientBase()}/posting-accounts`);
  }

  accounts(): Observable<ChartAccount[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<ChartAccount[]>(`${this.clientBase()}/accounts`);
  }

  setModule(moduleKey: string, slots: Record<string, string>): Observable<unknown> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put(`${this.clientBase()}/posting-accounts/${moduleKey}`, { slots });
  }
}
