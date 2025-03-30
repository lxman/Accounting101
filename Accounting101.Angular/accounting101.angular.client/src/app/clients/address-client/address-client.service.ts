import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../../services/user-data/user-data.service';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';

@Injectable({
  providedIn: 'root'
})
export class AddressClient {
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly http: HttpClient = inject(HttpClient);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  private baseUrl = this.globals.baseServerUrl;

  getStates(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/address/states`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getCountries(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/address/countries`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createAddress(address: any): Observable<string> {
    return this.http.post<any>(`${this.baseUrl}/address/${this.userData.get(this.globals.userIdKey)}`, address, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
