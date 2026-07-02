import { Injectable, Signal, computed, inject } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { HttpClient } from '@angular/common/http';
import { Observable, of, switchMap, catchError } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { DevIdentityService } from '../api/dev-identity.service';
import { CapabilitiesResponse, EMPTY_CAPABILITIES } from './capabilities';

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

  private readonly key = computed(() => {
    const clientId = this.client.clientId();
    return clientId ? { clientId, sub: this.identity.active().sub } : null;
  });

  private readonly response = toSignal(
    toObservable(this.key).pipe(
      switchMap((k): Observable<CapabilitiesResponse> =>
        k
          ? this.http
              .get<CapabilitiesResponse>(`${environment.apiBaseUrl}/clients/${k.clientId}/me/capabilities`)
              .pipe(catchError(() => of(EMPTY_CAPABILITIES)))
          : of(EMPTY_CAPABILITIES)),
    ),
    { initialValue: EMPTY_CAPABILITIES },
  );

  readonly capabilities: Signal<ReadonlySet<string>> = computed(() => new Set(this.response().capabilities));
  readonly roles: Signal<string[]> = computed(() => this.response().roles);
  readonly deploymentAdmin: Signal<boolean> = computed(() => this.response().deploymentAdmin);

  has(capability: string): boolean { return this.capabilities().has(capability); }

  /** True if the user holds any capability in the given area (e.g. "ar" matches "ar.read"/"ar.write"). */
  hasArea(area: string): boolean {
    const prefix = area + '.';
    for (const c of this.capabilities()) if (c.startsWith(prefix)) return true;
    return false;
  }
}
