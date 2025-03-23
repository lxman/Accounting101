import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { CreateUserModel } from '../../models/create-user.model';
import { LoginModel } from '../../models/login.model';
import { catchError } from 'rxjs/operators';
import { GlobalConstantsService } from '../global-constants/global-constants.service';
import { ApplicationUser } from '../../models/application-user.model';
import { UserDataService } from '../user-data/user-data.service';

@Injectable({
  providedIn: 'root'
})
export class UserManagerService {
  private readonly http: HttpClient = inject(HttpClient);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);
  private readonly userData: UserDataService = inject(UserDataService);

  private baseUrl = this.globals.baseServerUrl;

  isAuthenticated(): boolean {
    const result = this.userData.get(this.globals.userIdKey);
    return result !== null && result !== undefined && result !== '';
  }

  registerUser(user: CreateUserModel): Observable<CreateUserModel> {
    return this.http.post<CreateUserModel>(`${this.baseUrl}/authorization/register`, user)
    .pipe(catchError(this.handleError));
  }

  loginUser(user: LoginModel): Observable<ApplicationUser> {
    return this.http.post<ApplicationUser>(`${this.baseUrl}/authorization/login`, user, { withCredentials: true })
    .pipe(catchError(this.handleError));
  }

  logoutUser(): Observable<null> {
    return this.http.get<null>(`${this.baseUrl}/authorization/logout`, { withCredentials: true })
    .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
