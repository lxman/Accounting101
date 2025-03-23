import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';
import { GlobalConstantsService } from '../global-constants/global-constants.service';
import {RootGroups} from '../../models/root-groups.model';
import {AccountModel} from '../../models/account.model';

@Injectable({
  providedIn: 'root'
})
export class AccountsManagerService {
  private readonly userDataService = inject(UserDataService);
  private readonly globals = inject(GlobalConstantsService);
  private readonly client = inject(HttpClient);

  accountsExist(): Observable<boolean> {
    return this.client.get<boolean>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}/exist`,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getLayout(): Observable<RootGroups> {
    return this.client.get<RootGroups>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}/layout`,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getAccounts() : Observable<AccountModel[]> {
    return this.client.get<any>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}`,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
