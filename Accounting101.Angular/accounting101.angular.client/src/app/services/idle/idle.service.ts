import {inject, Injectable, OnInit} from '@angular/core';
import { Idle, DEFAULT_INTERRUPTSOURCES } from '@ng-idle/core';
import { UserManagerService } from '../user-manager/user-manager.service';
import { UserDataService } from '../user-data/user-data.service';
import { GlobalConstantsService } from '../global-constants/global-constants.service';
import { Router } from '@angular/router';

@Injectable({
  providedIn: 'root'
})

export class IdleService implements OnInit{
  private readonly router: Router = inject(Router);
  private readonly idle: Idle = inject(Idle);
  private readonly userManager: UserManagerService = inject(UserManagerService);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  initialize() {
    this.idle.setIdle(this.globals.idleTimeout);
    this.idle.setTimeout(this.globals.loginTimeout);
    this.idle.setInterrupts(DEFAULT_INTERRUPTSOURCES);
    this.idle.onTimeout.subscribe(() => {
      this.userManager.logoutUser();
      this.userData.clearData();
      this.router.navigate(['/']);
    });
    this.idle.onIdleEnd.subscribe(() => {});
    this.idle.onIdleStart.subscribe(() => {});
    this.idle.onTimeoutWarning.subscribe(() => {});
  }

  reset() {
    this.idle.watch();
  }

  ngOnInit() {
    this.reset();
    this.idle.watch();
  }
}
