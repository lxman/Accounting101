import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import {Observable, throwError} from 'rxjs';
import { catchError } from 'rxjs/operators';
import { UserDataService } from '../user-data/user-data.service';
import { GlobalConstantsService } from '../global-constants/global-constants.service';
import {ChartItemModel} from '../../models/chart-item.model';
import {CreateCoaRequest} from '../../models/create-coa-request.model';

@Injectable({
  providedIn: 'root'
})
export class ChartOfAccountsService {
  private readonly client: HttpClient = inject(HttpClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);

  private baseUrl = this.globals.baseServerUrl;

  getAvailableChartNames() : Observable<string[]> {
    return this.client.get<string[]>(`${this.baseUrl}/coa/available-names`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  getDescription(name: string) : Observable<ChartItemModel> {
    return this.client.get<ChartItemModel>(`${this.baseUrl}/coa/description/${name}`, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  createCoA(name: string, userId: string, clientId: string) : Observable<null> {
    const request = new CreateCoaRequest();
    request.name = name;
    request.dbName = userId;
    request.clientId = clientId;
    return this.client.post<null>(`${this.baseUrl}/coa/create`, request, { withCredentials: true })
      .pipe(catchError(this.handleError));
  }

  private handleError(error: any) {
    console.error(error);
    return throwError(() => error);
  }
}
