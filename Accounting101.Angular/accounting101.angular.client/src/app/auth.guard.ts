import {inject, Injectable} from '@angular/core';
import {
  CanActivate,
  Router
} from '@angular/router';
import { UserManagerService } from './services/user-manager/user-manager.service';

@Injectable({
  providedIn: 'root',
})

export class AuthGuard implements CanActivate {
  userManager: UserManagerService = inject(UserManagerService);
  constructor(private router: Router) {}

  canActivate(): boolean {
    const isAuthenticated = this.userManager.isAuthenticated();
    if (!isAuthenticated) {
      void this.router.navigate(['/']);
    }
    return isAuthenticated;
  }
}
