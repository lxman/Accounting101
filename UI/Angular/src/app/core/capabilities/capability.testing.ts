import { Provider, Signal, signal } from '@angular/core';
import { CapabilityService } from './capability.service';

/** Test double for CapabilityService with a fixed, mutable capability set. */
export class StubCapabilityService {
  private readonly _caps = signal<ReadonlySet<string>>(new Set());
  private readonly _loaded = signal(true);
  private readonly _deploymentAdmin = signal(false);
  readonly loaded: Signal<boolean> = this._loaded.asReadonly();
  readonly capabilities: Signal<ReadonlySet<string>> = this._caps.asReadonly();
  readonly roles: Signal<string[]> = signal([]);
  readonly deploymentAdmin: Signal<boolean> = this._deploymentAdmin.asReadonly();

  set(caps: string[]): void { this._caps.set(new Set(caps)); }
  setLoaded(loaded: boolean): void { this._loaded.set(loaded); }
  setDeploymentAdmin(deploymentAdmin: boolean): void { this._deploymentAdmin.set(deploymentAdmin); }
  has(capability: string): boolean { return this._caps().has(capability); }
  hasArea(area: string): boolean {
    const prefix = area + '.';
    for (const c of this._caps()) if (c.startsWith(prefix)) return true;
    return false;
  }
  reload(): void { /* no-op stub; spied on in interceptor/poll tests */ }
}

/** Provider granting the given capabilities to components under test (no HttpClient needed). */
export function provideCapabilities(...caps: string[]): Provider {
  const stub = new StubCapabilityService();
  stub.set(caps);
  return { provide: CapabilityService, useValue: stub };
}
