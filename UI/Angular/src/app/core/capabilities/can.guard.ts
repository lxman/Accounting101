import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/** Guards a write route. The required capability and the fallback route live in the route's `data`
 * (`requiredCapability`, `fallback`) — the single source shared with the live route sentinel. */
export const canWrite: CanActivateFn = (route: ActivatedRouteSnapshot): Observable<boolean | UrlTree> => {
  const caps = inject(CapabilityService);
  const router = inject(Router);
  const capability = route.data['requiredCapability'] as string;
  const fallback = route.data['fallback'] as string;
  return toObservable(caps.loaded).pipe(
    filter((loaded) => loaded),
    take(1),
    map(() => (caps.has(capability) ? true : router.parseUrl(fallback))),
  );
};
