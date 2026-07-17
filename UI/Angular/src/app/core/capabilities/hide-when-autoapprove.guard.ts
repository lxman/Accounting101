import { inject } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/** Redirects away from a route that is meaningless under AutoApprove (e.g. the pending-approvals
 * queue — always empty when entries post straight to the books). The fallback route lives in the
 * route's `data.fallback` (defaults to `/journal`). Waits for capabilities to load before deciding. */
export const hideWhenAutoApproveGuard: CanActivateFn = (
  route: ActivatedRouteSnapshot,
): Observable<boolean | UrlTree> => {
  const caps = inject(CapabilityService);
  const router = inject(Router);
  const fallback = (route.data['fallback'] as string) ?? '/journal';
  return toObservable(caps.loaded).pipe(
    filter((loaded) => loaded),
    take(1),
    map(() => (caps.approvalMode() === 'AutoApprove' ? router.parseUrl(fallback) : true)),
  );
};
