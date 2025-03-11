import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';

@Injectable({
  providedIn: 'root'
})
export class AddressManagerService {
  private baseUrl = 'https://localhost:7165';

  constructor(
    private readonly userData: UserDataService,
    private http: HttpClient) { }

  getStates(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/address/states`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getCountries(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/address/countries`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createAddress(address: any): Observable<string> {
    return this.http.post<any>(`${this.baseUrl}/address/${this.userData.get('userId')}`, address, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
