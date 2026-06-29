import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse, Posting } from './entry';

@Injectable({ providedIn: 'root' })
export class EntriesService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  listPaged(opts: { posting?: Posting; skip: number; limit: number }) {
    const id = this.client.clientId();
    let params = new HttpParams().set('skip', opts.skip).set('limit', opts.limit);
    if (opts.posting) params = params.set('posting', opts.posting);
    return this.http.get<PagedResponse<EntryResponse>>(
      `${environment.apiBaseUrl}/clients/${id}/entries`,
      { params },
    );
  }
}
