import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';
import { ClientModel } from '../../../Models/client.model';

@Injectable({
  providedIn: 'root'
})
export class ClientManagerService {
  private baseUrl = 'https://localhost:7165';

  constructor(
    private client: HttpClient,
    private userData: UserDataService) {}

  getClientsExist(): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/clients/exist/${this.userData.get('userId')}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getClients(): Observable<ClientModel[]> {
    return this.client.get<ClientModel[]>(`${this.baseUrl}/clients/${this.userData.get('userId')}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
