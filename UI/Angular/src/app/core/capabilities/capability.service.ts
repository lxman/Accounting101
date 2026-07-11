import { Injectable, Signal, computed, inject, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { HttpClient } from '@angular/common/http';
import { Observable, of, switchMap, catchError } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { DevIdentityService } from '../api/dev-identity.service';
import { CapabilitiesResponse, EMPTY_CAPABILITIES } from './capabilities';

/** Sentinel distinguishing "not yet loaded" from a loaded-but-empty response. */
const LOADING = Symbol('capabilities-loading');

/**
 * The acting user's resolved capabilities on the active client — the single source of truth the
 * sidebar (and, later, screens) use to decide what is visible/enabled. Re-resolves whenever the
 * client or the acting identity (the "Acting as" switcher) changes; a 403 / no client yields an
 * empty set (nothing beyond always-visible destinations).
 */
@Injectable({ providedIn: 'root' })
export class CapabilityService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly identity = inject(DevIdentityService);

  private readonly reloadTick = signal(0);

  private readonly key = computed(() => {
    const clientId = this.client.clientId();
    return clientId ? { clientId, sub: this.identity.active().sub } : null;
  });

  // Re-fetch when the identity/client key changes OR when reload() bumps the tick.
  private readonly fetchTrigger = computed(() => ({ key: this.key(), tick: this.reloadTick() }));

  private readonly response = toSignal<CapabilitiesResponse | typeof LOADING, typeof LOADING>(
    toObservable(this.fetchTrigger).pipe(
      switchMap(({ key }): Observable<CapabilitiesResponse> =>
        key
          ? this.http
              .get<CapabilitiesResponse>(`${environment.apiBaseUrl}/clients/${key.clientId}/me/capabilities`)
              .pipe(catchError(() => of(EMPTY_CAPABILITIES)))
          : of(EMPTY_CAPABILITIES)),
    ) as Observable<CapabilitiesResponse | typeof LOADING>,
    { initialValue: LOADING },
  );

  /** False until the first /me/capabilities response for the current key resolves. */
  readonly loaded: Signal<boolean> = computed(() => this.response() !== LOADING);

  private readonly current = computed<CapabilitiesResponse>(() => {
    const r = this.response();
    return r === LOADING ? EMPTY_CAPABILITIES : r;
  });

  readonly capabilities: Signal<ReadonlySet<string>> = computed(() => new Set(this.current().capabilities));
  readonly roles: Signal<string[]> = computed(() => this.current().roles);
  readonly deploymentAdmin: Signal<boolean> = computed(() => this.current().deploymentAdmin);
  readonly enabledModules: Signal<ReadonlySet<string>> = computed(() => new Set(this.current().enabledModules));

  has(capability: string): boolean { return this.capabilities().has(capability); }

  /** True if the user holds any capability in the given area (e.g. "ar" matches "ar.read"/"ar.write"). */
  hasArea(area: string): boolean {
    const prefix = area + '.';
    for (const c of this.capabilities()) if (c.startsWith(prefix)) return true;
    return false;
  }

  /** True if the given module key is enabled for the active client. */
  moduleEnabled(key: string): boolean { return this.enabledModules().has(key); }

  /** Force a re-fetch of the current client's capabilities (e.g. after a 403, or on a poll tick). */
  reload(): void { this.reloadTick.update((n) => n + 1); }
}
