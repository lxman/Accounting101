import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { AuditRecordResponse, AuditVerifyResponse } from './audit';
import { PagedResponse } from '../api/paged-response';

@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  entryAudit(entryId: string) {
    return this.http.get<AuditRecordResponse[]>(
      `${environment.apiBaseUrl}/clients/${this.client.clientId()}/audit/${entryId}`,
    );
  }

  clientAudit(skip: number, limit: number): Observable<PagedResponse<AuditRecordResponse>> {
    return this.http.get<PagedResponse<AuditRecordResponse>>(
      `${environment.apiBaseUrl}/clients/${this.client.clientId()}/audit?skip=${skip}&limit=${limit}`,
    );
  }

  verify(): Observable<AuditVerifyResponse> {
    return this.http.get<AuditVerifyResponse>(
      `${environment.apiBaseUrl}/clients/${this.client.clientId()}/audit/verify`,
    );
  }

  authorOf(records: AuditRecordResponse[]): string | null {
    return records.find(r => r.action === 'Created')?.actor.userId ?? null;
  }
}
