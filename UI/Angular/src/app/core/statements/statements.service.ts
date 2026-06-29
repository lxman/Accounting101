import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { BalanceSheetResponse, IncomeStatementResponse } from './statement';

@Injectable({ providedIn: 'root' })
export class StatementsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  balanceSheet(asOf?: string) {
    const id = this.client.clientId();
    let params = new HttpParams();
    if (asOf) params = params.set('asOf', asOf);
    return this.http.get<BalanceSheetResponse>(
      `${environment.apiBaseUrl}/clients/${id}/statements/balance-sheet`,
      { params },
    );
  }

  incomeStatement(from: string, to: string) {
    const id = this.client.clientId();
    const params = new HttpParams().set('from', from).set('to', to);
    return this.http.get<IncomeStatementResponse>(
      `${environment.apiBaseUrl}/clients/${id}/statements/income-statement`,
      { params },
    );
  }
}
