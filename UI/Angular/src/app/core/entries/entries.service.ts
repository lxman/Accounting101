import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { PagedResponse } from '../api/paged-response';
import {
  EntryResponse,
  EntryValidationResponse,
  Posting,
  PostEntryRequest,
  PostEntryResponse,
} from './entry';

@Injectable({ providedIn: 'root' })
export class EntriesService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private url(path = ''): string {
    return `${environment.apiBaseUrl}/clients/${this.client.clientId()}/entries${path}`;
  }

  listPaged(opts: { posting?: Posting; skip: number; limit: number }) {
    let params = new HttpParams().set('skip', opts.skip).set('limit', opts.limit);
    if (opts.posting) params = params.set('posting', opts.posting);
    return this.http.get<PagedResponse<EntryResponse>>(this.url(), { params });
  }

  post(req: PostEntryRequest) { return this.http.post<PostEntryResponse>(this.url(), req); }
  validate(req: PostEntryRequest) { return this.http.post<EntryValidationResponse>(this.url('/validate'), req); }
  approve(id: string) { return this.http.post<EntryResponse>(this.url(`/${id}/approve`), {}); }
  void(id: string, reason?: string) { return this.http.post<EntryResponse>(this.url(`/${id}/void`), { reason: reason ?? null }); }
  get(id: string) { return this.http.get<EntryResponse>(this.url(`/${id}`)); }
}
