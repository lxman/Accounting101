import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { BusinessModel } from '../../models/business.model';
import { UserDataService } from '../user-data/user-data.service';

@Injectable({
  providedIn: 'root'
})
export class BusinessManagerService {
  private baseUrl = 'https://localhost:7165';
  private userId: string = '';

  constructor(
    private client: HttpClient,
    private userData: UserDataService) {}

  businessExists(): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/business/exists/${this.userData.get('userId')}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getBusiness(): Observable<BusinessModel | null> {
    return this.client.get<BusinessModel | null>(`${this.baseUrl}/business/${this.userData.get('userId')}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createBusiness(business: BusinessModel): Observable<BusinessModel> {
    return this.client.post<BusinessModel>(`${this.baseUrl}/business/${this.userData.get('userId')}`, business, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
