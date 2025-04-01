import {inject, Injectable, OnInit} from '@angular/core';
import { Idle, DEFAULT_INTERRUPTSOURCES } from '@ng-idle/core';
import { UserClient } from '../../clients/user-client/user-client.service';
import { UserDataService } from '../user-data/user-data.service';
import { GlobalConstantsService } from '../global-constants/global-constants.service';
import { Router } from '@angular/router';

@Injectable({
  providedIn: 'root'
})

export class IdleService implements OnInit{
  private readonly router: Router = inject(Router);
  private readonly idle: Idle = inject(Idle);
  private readonly userManager: UserClient = inject(UserClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  initialize() {
    this.idle.setIdle(this.globals.idleTimeout);
    this.idle.setTimeout(this.globals.loginTimeout);
    this.idle.setInterrupts(DEFAULT_INTERRUPTSOURCES);
    this.idle.onTimeout.subscribe(() => {
      console.log('Timed out at ' + new Date());
      this.userManager.logoutUser();
      this.userData.clearData();
      void this.router.navigate(['/']);
    });
    this.idle.onIdleEnd.subscribe(() => {
      console.log('Back to active at ' + new Date());
    });
    this.idle.onIdleStart.subscribe(() => {
      console.log('Going idle at ' + new Date());
      // this.userManager.logoutUser();
    });
    this.idle.onTimeoutWarning.subscribe(() => {
      console.log('Timeout warning at ' + new Date());
      // this.userManager.logoutUser();
    });
  }

  reset() {
    this.idle.watch();
  }

  ngOnInit() {
    this.reset();
    this.idle.watch();
  }
}
