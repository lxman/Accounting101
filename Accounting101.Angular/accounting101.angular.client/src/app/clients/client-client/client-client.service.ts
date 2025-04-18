import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import {map, Observable, throwError} from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../../services/user-data/user-data.service';
import { ClientModel } from '../../models/client.model';
import { CreateClientModel } from '../../models/create-client.model';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';
import {PersonNameModel} from '../../models/person-name.model';
import {UsAddressModel} from '../../models/us-address.model';
import {ForeignAddressModel} from '../../models/foreign-address.model';

@Injectable({
  providedIn: 'root'
})
export class ClientClient {
  private readonly client: HttpClient = inject(HttpClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  private baseUrl = this.globals.baseServerUrl;

  getClientsExist(): Observable<boolean> {
    return this.client.get<boolean>(`${this.baseUrl}/clients/${this.userData.get(this.globals.userIdKey)}/exist`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getClients(): Observable<ClientModel[]> {
    return this.client
      .get<ClientModel[]>(`${this.baseUrl}/clients/${this.userData.get(this.globals.userIdKey)}`, { withCredentials: true })
      .pipe(
        map((clients: any[]) =>
          clients.map(client => {
            return this.buildClientModel(client);
          })
        ), // End of transformation
        catchError(this.handleError)
      );
  }

  createClient(clientModel: CreateClientModel): Observable<CreateClientModel> {
    return this.client.post<CreateClientModel>(`${this.baseUrl}/clients/${this.userData.get(this.globals.userIdKey)}`, clientModel, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  deleteClient(clientId: string): Observable<boolean> {
    return this.client.delete<boolean>(`${this.baseUrl}/clients/${this.userData.get(this.globals.userIdKey)}/${clientId}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getClient(clientId: string): Observable<ClientModel> {
    return this.client.get<ClientModel>(`${this.baseUrl}/clients/${this.userData.get(this.globals.userIdKey)}/${clientId}`, { withCredentials: true })
      .pipe(
        map((client: any) => {
          return this.buildClientModel(client);
        }),
        catchError(this.handleError)
      );
  }

  private buildClientModel(client: any): ClientModel {
    const newClient = new ClientModel(
      client.businessName,
      client.contactName ? Object.assign(new PersonNameModel(), client.contactName) : new PersonNameModel(),
      client.address
    );

    // Handle any additional fields if necessary
    newClient.id = client.id;
    newClient.usAddress = client.usAddress
      ? Object.assign(new UsAddressModel(
                                      client.usAddress.line1,
                                      client.usAddress.line2,
                                      client.usAddress.city,
                                      client.usAddress.state,
                                      client.usAddress.zip), client.usAddress)
      : null;
    newClient.foreignAddress = client.foreignAddress
      ? Object.assign(new ForeignAddressModel(
                                      client.foreignAddress.line1,
                                      client.foreignAddress.line2,
                                      client.foreignAddress.province,
                                      client.foreignAddress.postCode,
                                      client.foreignAddress.country
      ))
      : null;

    return newClient;
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
