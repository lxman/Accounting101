import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { ApprovalMode, ApprovalPolicy } from './approval-policy';

@Injectable({ providedIn: 'root' })
export class ApprovalPolicyService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(): string {
    return `${environment.apiBaseUrl}/clients/${this.client.clientId()}/approval-policy`;
  }

  get(): Observable<ApprovalPolicy> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<ApprovalPolicy>(this.base());
  }

  set(mode: ApprovalMode): Observable<ApprovalPolicy> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<ApprovalPolicy>(this.base(), { mode });
  }
}
