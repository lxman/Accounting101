import { HttpInterceptorFn } from '@angular/common/http';
import { environment } from './environment';
import { encodeDevToken } from './dev-token';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!environment.devUserId) return next(req);
  const token = encodeDevToken({ sub: environment.devUserId, name: environment.devUserName, claims: environment.devClaims });
  return next(req.clone({ setHeaders: { Authorization: `DevToken ${token}` } }));
};
