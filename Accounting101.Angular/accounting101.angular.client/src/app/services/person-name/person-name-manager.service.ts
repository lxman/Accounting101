import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';
import { PersonNameModel } from '../../models/person-name.model';
import { GlobalConstantsService } from '../global-constants/global-constants.service';

@Injectable({
  providedIn: 'root'
})
export class PersonNameManagerService {
  private readonly client: HttpClient = inject(HttpClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  private baseUrl = this.globals.baseServerUrl;

  createPersonName(personNameModel: PersonNameModel): Observable<PersonNameModel> {
    return this.client.post<PersonNameModel>(`${this.baseUrl}/person-name/${this.userData.get(this.globals.userIdKey)}`, personNameModel, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
