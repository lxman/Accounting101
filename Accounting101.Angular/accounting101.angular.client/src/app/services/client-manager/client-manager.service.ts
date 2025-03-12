import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';
import { ClientModel } from '../../models/client.model';
import { CreateClientModel } from '../../models/create-client.model';
import { GlobalConstantsService } from '../global-constants/global-constants.service';

@Injectable({
  providedIn: 'root'
})
export class ClientManagerService {
  private readonly client: HttpClient = inject(HttpClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  private baseUrl = this.globals.baseAppUrl;

  getClientsExist(): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/clients/exist/${this.userData.get(this.globals.userIdKey)}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getClients(): Observable<ClientModel[]> {
    return this.client.get<ClientModel[]>(`${this.baseUrl}/clients/${this.userData.get(this.globals.userIdKey)}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createClient(clientModel: CreateClientModel): Observable<CreateClientModel> {
    return this.client.post<CreateClientModel>(`${this.baseUrl}/clients/${this.userData.get(this.globals.userIdKey)}`, clientModel, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
