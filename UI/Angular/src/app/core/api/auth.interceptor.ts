import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { encodeDevToken } from './dev-token';
import { DevIdentityService } from './dev-identity.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const id = inject(DevIdentityService).active();
  if (!id.sub) return next(req);
  const token = encodeDevToken({ sub: id.sub, name: id.name, claims: id.claims });
  return next(req.clone({ setHeaders: { Authorization: `DevToken ${token}` } }));
};
