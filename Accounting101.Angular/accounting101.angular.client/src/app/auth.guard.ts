import {inject, Injectable} from '@angular/core';
import {
  CanActivate,
  Router
} from '@angular/router';
import { UserClient } from './clients/user-client/user-client.service';

@Injectable({
  providedIn: 'root',
})

export class AuthGuard implements CanActivate {
  userManager: UserClient = inject(UserClient);
  constructor(private router: Router) {}

  canActivate(): boolean {
    const isAuthenticated = this.userManager.isAuthenticated();
    if (!isAuthenticated) {
      void this.router.navigate(['/']);
    }
    return isAuthenticated;
  }
}
