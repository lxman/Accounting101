import { Injectable, signal } from '@angular/core';

@Injectable({ providedIn: 'root' })
export class ClientContextService {
  private readonly _clientId = signal<string | null>(null);
  readonly clientId = this._clientId.asReadonly();

  select(id: string | null): void {
    this._clientId.set(id);
  }
}
