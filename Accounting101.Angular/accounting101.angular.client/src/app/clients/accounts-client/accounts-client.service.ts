import { inject, Injectable } from '@angular/core';
import {HttpClient} from '@angular/common/http';
import { Observable, throwError, Subject, BehaviorSubject } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../../services/user-data/user-data.service';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';
import {RootGroups} from '../../models/root-groups.model';
import {AccountModel} from '../../models/account.model';
import {TransactionModel} from '../../models/transaction.model';
import {StreamedTransactionModel} from '../../models/streamed-transaction.model';

@Injectable({
  providedIn: 'root'
})
export class AccountsClient {
  private readonly userDataService = inject(UserDataService);
  private readonly globals = inject(GlobalConstantsService);
  private readonly client = inject(HttpClient);
  private transactionStreamSubject = new BehaviorSubject<StreamedTransactionModel[]>([]);
  public transactionStream$ = this.transactionStreamSubject.asObservable();

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

  getTransactionsForAccount(accountId: string): Observable<StreamedTransactionModel[]> {
    // Validate the account ID
    if (!accountId || accountId === '00000000-0000-0000-0000-000000000000') {
      console.error('Invalid account ID provided to getTransactionsForAccount:', accountId);
      return new Observable<StreamedTransactionModel[]>(observer => {
        observer.next([]);
        observer.complete();
      });
    }

    return this.client.get<StreamedTransactionModel[]>(
      `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${accountId}/transactions`,
      { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  /**
   * Streams transactions for an account and updates the transaction stream observable
   * as new transactions are received.
   * @param accountId - The ID of the account to stream transactions for
   */
  streamTransactionsForAccount(accountId: string): Observable<StreamedTransactionModel[]> {
    // Validate the account ID
    if (!accountId || accountId === '00000000-0000-0000-0000-000000000000') {
      console.error('Invalid account ID provided to streamTransactionsForAccount:', accountId);
      this.transactionStreamSubject.next([]);
      this.transactionStreamSubject.complete();
      return this.transactionStream$;
    }

    // Reset the behavior subject with an empty array
    this.transactionStreamSubject.next([]);

    const url = `${this.globals.baseServerUrl}/accounts/${this.userDataService.get(this.globals.userIdKey)}/${accountId}/transactions`;


    let allTransactions: StreamedTransactionModel[] = [];

    // Check if baseServerUrl is configured
    if (!this.globals.baseServerUrl) {
      console.error('API URL is not configured. Please check environment settings.');
      this.transactionStreamSubject.error(new Error('API URL is not configured'));
      return this.transactionStream$;
    }

    fetch(url, {
      method: 'GET',
      credentials: 'include',
      headers: {
        'Accept': 'application/json'
      }
    })
    .then(response => {
            if (!response.ok) {
        throw new Error(`HTTP error! status: ${response.status}`);
      }

      // For non-streaming API fallback - if the response is not streaming
      if (!response.body) {
                response.json().then(data => {
          this.transactionStreamSubject.next(data);
          this.transactionStreamSubject.complete();
        });
        return; // Return early, don't continue with streaming logic
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      const processStream = () => {
        reader.read().then(({ done, value }) => {
          if (done) {
            // Handle any remaining data in buffer
            if (buffer.trim()) {
              try {
                const batch = JSON.parse(buffer);
                if (Array.isArray(batch)) {
                  allTransactions = [...allTransactions, ...batch];
                  this.transactionStreamSubject.next(allTransactions);
                                  }
              } catch (e) {
                console.error('Error parsing final JSON batch:', e);
              }
            }
                        this.transactionStreamSubject.complete();
            return;
          }

          // Add the new chunk to our buffer
          const chunk = decoder.decode(value, { stream: true });
          buffer += chunk;


          // Process complete JSON arrays separated by newlines
          const lines = buffer.split('\n');
          // Keep the last (potentially incomplete) line in buffer
          buffer = lines.pop() || '';

          for (const line of lines) {
            if (line.trim()) {
              try {
                const batch = JSON.parse(line);
                if (Array.isArray(batch)) {
                  allTransactions = [...allTransactions, ...batch];
                  // Emit the updated list of all transactions so far
                  this.transactionStreamSubject.next(allTransactions);
                                  }
              } catch (e) {
                console.error('Error parsing JSON batch:', e, 'Line:', line.substring(0, 100) + '...');
              }
            }
          }

          // Continue reading
          processStream();
        }).catch(err => {
          console.error('Stream reading error:', err);
          this.transactionStreamSubject.error(err);
        });
      };

      processStream();
    })
    .catch(error => {
      console.error('Fetch error:', error);
      this.transactionStreamSubject.error(error);

      // Fallback to standard request
            this.getTransactionsForAccount(accountId).subscribe({
        next: transactions => {
          this.transactionStreamSubject.next(transactions);
          this.transactionStreamSubject.complete();
        },
        error: fallbackError => {
          console.error('Fallback request failed:', fallbackError);
          this.transactionStreamSubject.error(fallbackError);
        }
      });
    });

    return this.transactionStream$;
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
