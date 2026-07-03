import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/** Allow only deployment admins; redirect others to `fallback` once capabilities have loaded. */
export function deploymentAdminGuard(fallback: string): CanActivateFn {
  return () => {
    const caps = inject(CapabilityService);
    const router = inject(Router);
    return toObservable(caps.loaded).pipe(
      filter((loaded) => loaded),
      take(1),
      map(() => (caps.deploymentAdmin() ? true : router.parseUrl(fallback))),
    );
  };
}
