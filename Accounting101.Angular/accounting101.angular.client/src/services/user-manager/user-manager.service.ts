import { Injectable } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { CreateUserModel } from '../../../Models/create-user.model';
import { LoginModel } from '../../../Models/login.model';
import { catchError } from 'rxjs/operators';

@Injectable({
  providedIn: 'root'
})
export class UserManagerService {
  public databaseId: string = '';
  private baseUrl = 'https://localhost:7165';
  private readonly headers = new HttpHeaders({
    'Content-Type': 'application/json',
    'X-Requested-With': 'XMLHttpRequest'
  });

  constructor(private http: HttpClient) {}

  registerUser(user: CreateUserModel): Observable<CreateUserModel> {
    return this.http.post<CreateUserModel>(`${this.baseUrl}/authorization/register`, user)
    .pipe(catchError(this.handleError));
  }

  loginUser(user: LoginModel): Observable<Object> {
    return this.http.post<LoginModel>(`${this.baseUrl}/authorization/login`, user, { headers: this.headers, withCredentials: true })
    .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
