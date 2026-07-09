import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, map } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import {
  ItemView, StockMovement, StockMovementView, SaveItemRequest, RecordMovementRequest,
  InventoryListQuery, MovementListQuery,
} from './inventory';

@Injectable({ providedIn: 'root' })
export class InventoryService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  // ── Items ─────────────────────────────────────────────────────────────────
  // NOTE (deviation from the banking service's unwrap-the-view convention): item endpoints wrap the
  // domain object as ItemView ({ item, averageUnitCost }), and averageUnitCost is data NOT present on
  // Item itself. Unwrapping to the raw Item (as banking's getDisbursement/getDeposit do for their
  // no-extra-data views) would silently drop that field, so item-returning methods below keep the
  // ItemView envelope intact rather than unwrap it.
  listItems(q: InventoryListQuery): Observable<PagedResponse<ItemView>> {
    if (!this.client.clientId()) return EMPTY;
    let params = new HttpParams().set('skip', q.skip).set('limit', q.limit);
    if (q.order) params = params.set('order', q.order);
    if (q.includeInactive !== undefined) params = params.set('includeInactive', q.includeInactive);
    return this.http.get<PagedResponse<ItemView>>(this.base('/items'), { params });
  }
  getItem(id: string): Observable<ItemView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<ItemView>(this.base(`/items/${id}`));
  }
  createItem(req: SaveItemRequest): Observable<ItemView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ItemView>(this.base('/items'), req);
  }
  updateItem(id: string, req: SaveItemRequest): Observable<ItemView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<ItemView>(this.base(`/items/${id}`), req);
  }
  deactivateItem(id: string): Observable<void> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<void>(this.base(`/items/${id}/deactivate`), {});
  }
  reactivateItem(id: string): Observable<ItemView> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ItemView>(this.base(`/items/${id}/reactivate`), {});
  }

  // ── Stock movements ──────────────────────────────────────────────────────
  // NOTE: movement endpoints wrap as StockMovementView ({ movement }), which — unlike ItemView — carries
  // no extra data beyond the entity itself. So, mirroring banking's getDisbursement/getDeposit, the
  // methods below unwrap the view and return the raw StockMovement.
  recordMovement(req: RecordMovementRequest): Observable<StockMovement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<StockMovementView>(this.base('/movements'), req).pipe(map(v => v.movement));
  }
  getMovement(id: string): Observable<StockMovement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<StockMovementView>(this.base(`/movements/${id}`)).pipe(map(v => v.movement));
  }
  listMovements(itemId: string, q: MovementListQuery): Observable<PagedResponse<StockMovement>> {
    if (!this.client.clientId()) return EMPTY;
    let params = new HttpParams().set('itemId', itemId).set('skip', q.skip).set('limit', q.limit);
    if (q.order) params = params.set('order', q.order);
    if (q.includeVoided !== undefined) params = params.set('includeVoided', q.includeVoided);
    return this.http.get<PagedResponse<StockMovementView>>(this.base('/movements'), { params }).pipe(
      map(p => ({ items: p.items.map(v => v.movement), total: p.total, skip: p.skip, limit: p.limit })));
  }
  voidMovement(id: string, reason?: string | null): Observable<StockMovement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<StockMovementView>(this.base(`/movements/${id}/void`), { reason: reason ?? null }).pipe(map(v => v.movement));
  }
}
