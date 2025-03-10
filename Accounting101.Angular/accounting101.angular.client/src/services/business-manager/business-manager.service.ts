import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { BusinessModel } from '../../../Models/business.model';
import { UserManagerService } from '../user-manager/user-manager.service';

@Injectable({
  providedIn: 'root'
})
export class BusinessManagerService {
  private baseUrl = 'https://localhost:7165';

  constructor(private client: HttpClient, private userManager: UserManagerService) { }

  businessExists(): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/business/exists/${this.userManager.databaseId}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getBusiness(): Observable<BusinessModel | null> {
    return this.client.get<BusinessModel | null>(`${this.baseUrl}/business/${this.userManager.databaseId}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createBusiness(business: BusinessModel): Observable<BusinessModel> {
    return this.client.post<BusinessModel>(`${this.baseUrl}/business/${this.userManager.databaseId}`, business, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
