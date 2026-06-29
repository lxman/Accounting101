import { HttpInterceptorFn } from '@angular/common/http';
import { environment } from './environment';

export const authInterceptor: HttpInterceptorFn = (req, next) =>
  environment.devToken
    ? next(req.clone({ setHeaders: { Authorization: `Bearer ${environment.devToken}` } }))
    : next(req);
