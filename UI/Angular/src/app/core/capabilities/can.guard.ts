import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { toObservable } from '@angular/core/rxjs-interop';
import { Observable, filter, map, take } from 'rxjs';
import { CapabilityService } from './capability.service';

/**
 * Route guard for write screens: waits until capabilities are loaded, then allows if the user holds
 * `capability`, else redirects to `fallback` (the area's list). The UI layer of defense; the backend
 * is the real gate.
 */
export function canWrite(capability: string, fallback: string): CanActivateFn {
  return (): Observable<boolean | UrlTree> => {
    const caps = inject(CapabilityService);
    const router = inject(Router);
    return toObservable(caps.loaded).pipe(
      filter((loaded) => loaded),
      take(1),
      map(() => (caps.has(capability) ? true : router.parseUrl(fallback))),
    );
  };
}
