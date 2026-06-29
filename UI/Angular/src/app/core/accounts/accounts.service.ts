import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable, tap } from 'rxjs';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { AccountResponse, AccountUpsert } from './account';

@Injectable({ providedIn: 'root' })
export class AccountsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly _accounts = signal<AccountResponse[]>([]);
  readonly accounts = this._accounts.asReadonly();
  readonly byId = computed(() => new Map(this._accounts().map(a => [a.id, a])));

  load(): void {
    const id = this.client.clientId(); if (!id) return;
    this.http.get<AccountResponse[]>(`${environment.apiBaseUrl}/clients/${id}/accounts`)
      .subscribe(a => this._accounts.set(a));
  }

  label(accountId: string): string {
    const a = this.byId().get(accountId); return a ? `${a.number} ${a.name}` : accountId;
  }

  upsert(a: AccountUpsert): Observable<AccountResponse> {
    const id = this.client.clientId();
    if (!id) return EMPTY;
    const body = { number: a.number, name: a.name, type: a.type, parentId: a.parentId,
      postable: a.postable, requiredDimension: a.requiredDimension, cashFlowActivity: a.cashFlowActivity,
      isRetainedEarnings: a.isRetainedEarnings, active: a.active };
    return this.http.put<AccountResponse>(`${environment.apiBaseUrl}/clients/${id}/accounts/${a.id}`, body)
      .pipe(tap(saved => this._accounts.update(list => {
        const i = list.findIndex(x => x.id === saved.id);
        return i >= 0 ? list.map(x => (x.id === saved.id ? saved : x)) : [...list, saved];
      })));
  }

  newId(): string { return crypto.randomUUID(); }
}
