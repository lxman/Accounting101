import { inject, Injectable } from '@angular/core';
import {HttpClient} from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../../services/user-data/user-data.service';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';
import {RootGroups} from '../../models/root-groups.model';
import {AccountModel} from '../../models/account.model';
import {TransactionModel} from '../../models/transaction.model';

@Injectable({
  providedIn: 'root'
})

export class AccountsClient {
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

  getTransactionsForAccount(accountId: string): Observable<TransactionModel[]> {
    return this.client.get<TransactionModel[]>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${accountId}/transactions`,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getBalanceOnDate(accountId: string, date: Date): Observable<number> {
    return this.client.post<number>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}/${accountId}/balance`,
      { date: date.toISOString() },
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createTransaction(transaction: TransactionModel): Observable<string> {
    return this.client.post<string>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}/transactions`,
      transaction,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  updateTransaction(transaction: TransactionModel): Observable<string> {
    return this.client.put<string>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}/transactions`,
      transaction,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  deleteTransaction(transactionId: string): Observable<boolean> {
    return this.client.delete<boolean>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}/transactions/${transactionId}`,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  saveLayout(layout: RootGroups): Observable<void> {
    return this.client.post<void>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${this.userDataService.get(this.globals.clientIdKey)}/layout`,
      layout,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
