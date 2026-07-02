import { Injectable, signal } from '@angular/core';
import { DEV_IDENTITIES } from './dev-identities';

export interface DevIdentity { sub: string; name: string; claims: { type: string; value: string }[]; }

@Injectable({ providedIn: 'root' })
export class DevIdentityService {
  readonly identities: readonly DevIdentity[] = DEV_IDENTITIES;
  private readonly _active = signal<DevIdentity>(this.identities[0]);
  readonly active = this._active.asReadonly();

  use(sub: string): void {
    const match = this.identities.find(i => i.sub === sub);
    if (match) this._active.set(match);
  }
}
