import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/**
 * Guards a route behind a paid module's entitlement. The module key and the fallback route live in
 * the route's `data` (`requiredModule`, `fallback`) — the same source the route sentinel reads.
 *
 * This is UX, not security: the endpoint's own EnabledModules check is the authority. Compose it
 * with `canWrite` when the route both writes and belongs to a module.
 */
export const moduleEnabled: CanActivateFn = (route: ActivatedRouteSnapshot): Observable<boolean | UrlTree> => {
  const caps = inject(CapabilityService);
  const router = inject(Router);
  const module = route.data['requiredModule'] as string;
  const fallback = route.data['fallback'] as string;
  return toObservable(caps.loaded).pipe(
    filter((loaded) => loaded),
    take(1),
    map(() => (caps.moduleEnabled(module) ? true : router.parseUrl(fallback))),
  );
};
