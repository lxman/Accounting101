import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { CapabilityService } from './capability.service';

/** On any 403 (except the capabilities fetch itself), refetch the caller's capabilities so the next
 * action reflects the revocation instantly. Rethrows the error untouched. */
export const capabilitySelfHealInterceptor: HttpInterceptorFn = (req, next) => {
  const caps = inject(CapabilityService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 403 && !req.url.includes('/me/capabilities')) {
        caps.reload();
      }
      return throwError(() => err);
    }),
  );
};
