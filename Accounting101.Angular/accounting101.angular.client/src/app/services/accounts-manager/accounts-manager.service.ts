import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';

@Injectable({
  providedIn: 'root'
})
export class AccountsManagerService {
  private readonly userDataService = inject(UserDataService);
  private readonly client = inject(HttpClient);
  private baseUrl = 'https://localhost:7165';

  accountsExist(dbId: string, clientId: string): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/accounts/exist/${dbId}/${clientId}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
