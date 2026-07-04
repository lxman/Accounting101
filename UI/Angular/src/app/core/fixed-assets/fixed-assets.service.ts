import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, map } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse } from '../entries/entry';
import {
  AssetView, DepreciationRun, DepreciationRunView, Disposal, DisposalView,
  SaveAssetRequest, RunDepreciationRequest, DisposeAssetRequest, FixedAssetsListQuery,
} from './fixed-assets';

@Injectable({ providedIn: 'root' })
export class FixedAssetsService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  private listParams(q: FixedAssetsListQuery): HttpParams {
    let p = new HttpParams().set('skip', q.skip).set('limit', q.limit);
    if (q.order) p = p.set('order', q.order);
    if (q.includeInactive) p = p.set('includeInactive', true);
    if (q.includeVoided) p = p.set('includeVoided', true);
    return p;
  }

  // ── Assets ────────────────────────────────────────────────────────────────
  listAssets(q: FixedAssetsListQuery): Observable<PagedResponse<AssetView>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<AssetView>>(this.base('/assets'), { params: this.listParams(q) });
  }
  getAsset(id: string): Observable<AssetView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<AssetView>(this.base(`/assets/${id}`));
  }
  createAsset(req: SaveAssetRequest): Observable<AssetView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<AssetView>(this.base('/assets'), req);
  }
  updateAsset(id: string, req: SaveAssetRequest): Observable<AssetView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<AssetView>(this.base(`/assets/${id}`), req);
  }
  disposeAsset(id: string, req: DisposeAssetRequest): Observable<Disposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DisposalView>(this.base(`/assets/${id}/dispose`), req).pipe(map(v => v.disposal));
  }

  // ── Depreciation runs ──────────────────────────────────────────────────────
  listRuns(q: FixedAssetsListQuery): Observable<PagedResponse<DepreciationRun>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<DepreciationRunView>>(this.base('/depreciation-runs'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.run) })));
  }
  getRun(id: string): Observable<DepreciationRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<DepreciationRunView>(this.base(`/depreciation-runs/${id}`)).pipe(map(v => v.run));
  }
  runDepreciation(req: RunDepreciationRequest): Observable<DepreciationRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DepreciationRunView>(this.base('/depreciation-runs'), req).pipe(map(v => v.run));
  }
  voidRun(id: string, reason?: string | null): Observable<DepreciationRun> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DepreciationRunView>(this.base(`/depreciation-runs/${id}/void`), { reason: reason ?? null }).pipe(map(v => v.run));
  }

  // ── Disposals ──────────────────────────────────────────────────────────────
  listDisposals(q: FixedAssetsListQuery): Observable<PagedResponse<Disposal>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<DisposalView>>(this.base('/disposals'), { params: this.listParams(q) })
      .pipe(map(pg => ({ ...pg, items: pg.items.map(v => v.disposal) })));
  }
  getDisposal(id: string): Observable<Disposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<DisposalView>(this.base(`/disposals/${id}`)).pipe(map(v => v.disposal));
  }
  voidDisposal(id: string, reason?: string | null): Observable<Disposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<DisposalView>(this.base(`/disposals/${id}/void`), { reason: reason ?? null }).pipe(map(v => v.disposal));
  }

  /** Posted journal entry(ies) for a fixed-assets document — powers the "posted journal entry" link. */
  entriesForSource(sourceRef: string): Observable<EntryResponse[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<EntryResponse[]>(this.base('/entries'), { params: new HttpParams().set('sourceRef', sourceRef) });
  }
}
