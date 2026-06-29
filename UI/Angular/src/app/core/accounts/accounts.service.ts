import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { AccountResponse } from './account';

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
}
