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

  getBusiness(): Observable<BusinessModel> {
    return this.client.get<BusinessModel>(`${this.baseUrl}/business/${this.userManager.id}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
