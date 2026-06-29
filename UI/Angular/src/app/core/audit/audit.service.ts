import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { AuditRecordResponse } from './audit';

@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  entryAudit(entryId: string) {
    return this.http.get<AuditRecordResponse[]>(
      `${environment.apiBaseUrl}/clients/${this.client.clientId()}/audit/${entryId}`,
    );
  }

  authorOf(records: AuditRecordResponse[]): string | null {
    return records.find(r => r.action === 'Created')?.actor.userId ?? null;
  }
}
