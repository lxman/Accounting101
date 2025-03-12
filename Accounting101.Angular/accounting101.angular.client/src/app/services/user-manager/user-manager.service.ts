import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { CreateUserModel } from '../../models/create-user.model';
import { LoginModel } from '../../models/login.model';
import { catchError } from 'rxjs/operators';
import { GlobalConstantsService } from '../global-constants/global-constants.service';

@Injectable({
  providedIn: 'root'
})
export class UserManagerService {
  private readonly http: HttpClient = inject(HttpClient);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  private baseUrl = this.globals.baseAppUrl;

  registerUser(user: CreateUserModel): Observable<CreateUserModel> {
    return this.http.post<CreateUserModel>(`${this.baseUrl}/authorization/register`, user)
    .pipe(catchError(this.handleError));
  }

  loginUser(user: LoginModel): Observable<Object> {
    return this.http.post<LoginModel>(`${this.baseUrl}/authorization/login`, user, { withCredentials: true })
    .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
