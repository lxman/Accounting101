import {inject, Injectable} from '@angular/core';
import {
  CanActivate,
  Router,
  ActivatedRouteSnapshot,
  RouterStateSnapshot,
} from '@angular/router';
import { UserManagerService } from './services/user-manager/user-manager.service';

@Injectable({
  providedIn: 'root',
})

export class AuthGuard implements CanActivate {
  userManager: UserManagerService = inject(UserManagerService);
  constructor(private router: Router) {}

  canActivate(
    route: ActivatedRouteSnapshot,
    state: RouterStateSnapshot
  ): boolean {
    const isAuthenticated = this.userManager.isAuthenticated();
    if (!isAuthenticated) {
      this.router.navigate(['/']);
    }
    return isAuthenticated;
  }
}
