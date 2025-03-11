import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

@Injectable({
  providedIn: 'root'
})
export class AddressManagerService {
  private baseUrl = 'https://localhost:7165';

  constructor(private http: HttpClient) { }

  getStates(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/address/states`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getCountries(): Observable<string[]> {
    return this.http.get<string[]>(`${this.baseUrl}/address/countries`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
