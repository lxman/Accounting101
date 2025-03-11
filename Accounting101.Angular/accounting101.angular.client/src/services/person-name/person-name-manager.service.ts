import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';
import { PersonNameModel } from '../../../Models/person-name.model';

@Injectable({
  providedIn: 'root'
})
export class PersonNameManagerService {
  private baseUrl = 'https://localhost:7165';

  constructor(
    private client: HttpClient,
    private userData: UserDataService) {}

  createPersonName(personNameModel: PersonNameModel): Observable<PersonNameModel> {
    return this.client.post<PersonNameModel>(`${this.baseUrl}/person-name/${this.userData.get('userId')}`, personNameModel, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
