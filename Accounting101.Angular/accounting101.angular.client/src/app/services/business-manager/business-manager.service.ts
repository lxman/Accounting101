import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { BusinessModel } from '../../models/business.model';
import { UserDataService } from '../user-data/user-data.service';
import { GlobalConstantsService } from '../global-constants/global-constants.service';

@Injectable({
  providedIn: 'root'
})
export class BusinessManagerService {
  private readonly client: HttpClient = inject(HttpClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  private baseUrl = this.globals.baseAppUrl;

  businessExists(): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/business/exists/${this.userData.get(this.globals.userIdKey)}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getBusiness(): Observable<BusinessModel | null> {
    return this.client.get<BusinessModel | null>(`${this.baseUrl}/business/${this.userData.get(this.globals.userIdKey)}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createBusiness(business: BusinessModel): Observable<BusinessModel> {
    return this.client.post<BusinessModel>(`${this.baseUrl}/business/${this.userData.get(this.globals.userIdKey)}`, business, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
