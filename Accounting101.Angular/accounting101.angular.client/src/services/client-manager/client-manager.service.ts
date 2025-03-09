import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserManagerService } from '../user-manager/user-manager.service';
import { ClientModel } from '../../../Models/client.model';

@Injectable({
  providedIn: 'root'
})
export class ClientManagerService {
  private baseUrl = 'https://localhost:7165';

  constructor(private client: HttpClient, private userService: UserManagerService) {}

  getClientsExist(): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/clients/exist/${this.userService.databaseId}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getClients(): Observable<ClientModel[]> {
    return this.client.get<ClientModel[]>(`${this.baseUrl}/clients/${this.userService.databaseId}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
