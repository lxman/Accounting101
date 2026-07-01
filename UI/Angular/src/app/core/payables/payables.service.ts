import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, map, tap } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { extractProblem } from '../api/problem-details';
import { Vendor, Bill, BillView, DraftBillRequest, BillListQuery, BillPayment, RecordBillPaymentRequest, VendorCreditApplication, ApplyVendorCreditRequest, VendorAccountView } from './payables';

@Injectable({ providedIn: 'root' })
export class PayablesService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);
  private readonly _vendors = signal<Vendor[]>([]);
  readonly vendors = this._vendors.asReadonly();
  private readonly byId = computed(() => new Map(this._vendors().map(v => [v.id, v])));
  private readonly _loadError = signal<string | null>(null);
  readonly loadError = this._loadError.asReadonly();

  // Selected vendor survives navigation (root singleton) and reload (per-client localStorage).
  private readonly _selectedVendorId = signal<string>('');
  readonly selectedVendorId = this._selectedVendorId.asReadonly();

  constructor() {
    effect(() => {
      const cid = this.client.clientId();
      this._selectedVendorId.set(cid ? (localStorage.getItem(this.vendorKey(cid)) ?? '') : '');
    });
  }

  private vendorKey(clientId: string): string { return `a101.pay.vendor.${clientId}`; }

  setSelectedVendor(id: string): void {
    this._selectedVendorId.set(id);
    const cid = this.client.clientId(); if (!cid) return;
    if (id) localStorage.setItem(this.vendorKey(cid), id);
    else localStorage.removeItem(this.vendorKey(cid));
  }

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }

  load(): void {
    const id = this.client.clientId(); if (!id) return;
    this.http.get<Vendor[]>(this.base('/vendors')).subscribe({
      next: vs => {
        this._vendors.set(vs);
        this._loadError.set(null);
        const sel = this._selectedVendorId();
        if (sel && !vs.some(v => v.id === sel)) this.setSelectedVendor('');
      },
      error: e => this._loadError.set(extractProblem(e).detail),
    });
  }

  create(name: string, email?: string | null): Observable<Vendor> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<Vendor>(this.base('/vendors'), { name, email: email ?? null })
      .pipe(tap(v => this._vendors.update(list => [...list, v])));
  }

  vendorName(id: string): string { return this.byId().get(id)?.name ?? id; }

  listBills(q: BillListQuery): Observable<PagedResponse<BillView>> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    let params = new HttpParams().set('vendorId', q.vendorId).set('skip', q.skip).set('limit', q.limit);
    if (q.settlement) params = params.set('settlement', q.settlement);
    if (q.order) params = params.set('order', q.order);
    return this.http.get<PagedResponse<BillView>>(this.base('/bills'), { params });
  }

  getBill(id: string): Observable<BillView> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.get<BillView>(this.base(`/bills/${id}`));
  }

  draftBill(req: DraftBillRequest): Observable<Bill> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<Bill>(this.base('/bills'), req);
  }

  enter(id: string): Observable<Bill> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<Bill>(this.base(`/bills/${id}/enter`), {});
  }

  editBill(id: string, req: DraftBillRequest): Observable<Bill> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.put<Bill>(this.base(`/bills/${id}`), req);
  }

  discardBill(id: string): Observable<void> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.delete<void>(this.base(`/bills/${id}`));
  }

  void(id: string, reason?: string | null): Observable<Bill> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<Bill>(this.base(`/bills/${id}/void`), { reason: reason ?? null });
  }

  listBillPayments(vendorId: string): Observable<BillPayment[]> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<BillPayment[]>(this.base('/bill-payments'), { params: new HttpParams().set('vendorId', vendorId) });
  }

  recordBillPayment(req: RecordBillPaymentRequest): Observable<BillPayment> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<BillPayment>(this.base('/bill-payments'), req);
  }

  voidBillPayment(id: string, reason?: string | null): Observable<BillPayment> {
    const clientId = this.client.clientId(); if (!clientId) return EMPTY;
    return this.http.post<BillPayment>(this.base(`/bill-payments/${id}/void`), { reason: reason ?? null });
  }

  vendorCreditBalance(vendorId: string): Observable<number> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<{ creditBalance: number }>(this.base(`/vendors/${vendorId}/credit-balance`))
      .pipe(map(r => r.creditBalance));
  }

  listVendorCreditApplications(vendorId: string): Observable<VendorCreditApplication[]> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<VendorCreditApplication[]>(this.base('/vendor-credit-applications'),
      { params: new HttpParams().set('vendorId', vendorId) });
  }

  applyVendorCredit(req: ApplyVendorCreditRequest): Observable<VendorCreditApplication> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.post<VendorCreditApplication>(this.base('/vendor-credit-applications'), req);
  }

  getVendorAccount(vendorId: string): Observable<VendorAccountView> {
    const id = this.client.clientId(); if (!id) return EMPTY;
    return this.http.get<VendorAccountView>(this.base(`/vendors/${vendorId}/account`));
  }
}
