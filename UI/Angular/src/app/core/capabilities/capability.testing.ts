import { Provider, Signal, signal } from '@angular/core';
import { CapabilityService } from './capability.service';
import { ApprovalMode } from '../approval-policy/approval-policy';

/** Test double for CapabilityService with a fixed, mutable capability set. */
export class StubCapabilityService {
  private readonly _caps = signal<ReadonlySet<string>>(new Set());
  private readonly _loaded = signal(true);
  private readonly _deploymentAdmin = signal(false);
  private readonly _enabledModules = signal<ReadonlySet<string>>(new Set());
  private readonly _approvalMode = signal<ApprovalMode>('TwoPerson');
  readonly loaded: Signal<boolean> = this._loaded.asReadonly();
  readonly capabilities: Signal<ReadonlySet<string>> = this._caps.asReadonly();
  readonly roles: Signal<string[]> = signal([]);
  readonly deploymentAdmin: Signal<boolean> = this._deploymentAdmin.asReadonly();
  readonly enabledModules: Signal<ReadonlySet<string>> = this._enabledModules.asReadonly();
  readonly approvalMode: Signal<ApprovalMode> = this._approvalMode.asReadonly();

  set(caps: string[]): void { this._caps.set(new Set(caps)); }
  setLoaded(loaded: boolean): void { this._loaded.set(loaded); }
  setDeploymentAdmin(deploymentAdmin: boolean): void { this._deploymentAdmin.set(deploymentAdmin); }
  setEnabledModules(modules: string[]): void { this._enabledModules.set(new Set(modules)); }
  setApprovalMode(mode: ApprovalMode): void { this._approvalMode.set(mode); }
  has(capability: string): boolean { return this._caps().has(capability); }
  hasArea(area: string): boolean {
    const prefix = area + '.';
    for (const c of this._caps()) if (c.startsWith(prefix)) return true;
    return false;
  }
  moduleEnabled(key: string): boolean { return this._enabledModules().has(key); }
  reload(): void { /* no-op stub; spied on in interceptor/poll tests */ }
}

/** Provider granting the given capabilities to components under test (no HttpClient needed). */
export function provideCapabilities(...caps: string[]): Provider {
  const stub = new StubCapabilityService();
  stub.set(caps);
  return { provide: CapabilityService, useValue: stub };
}
