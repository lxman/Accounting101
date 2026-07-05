import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Observable, forkJoin, map } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse } from '../entries/entry';
import {
  CashDisbursement, CashDeposit, CashDisbursementView, CashDepositView, CashVoucherRow,
  RecordCashVoucherRequest, BankingListQuery,
  BankStatement, RecordBankStatementRequest,
} from './banking';

@Injectable({ providedIn: 'root' })
export class BankingService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}${path}`; }
  private listParams(q: BankingListQuery): HttpParams {
    let p = new HttpParams().set('skip', q.skip).set('limit', q.limit);
    if (q.order) p = p.set('order', q.order);
    return p;
  }
  private sum(lines: { amount: number }[]): number { return lines.reduce((s, l) => s + l.amount, 0); }

  // ── Cash vouchers ──────────────────────────────────────────────────────────
  // NOTE (deviation from the task-2 brief): verified against
  // Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs — the record/void POST
  // endpoints return Results.Created(disbursement)/Results.Ok(voided), i.e. the RAW CashDisbursement/
  // CashDeposit domain object, NOT a CashDisbursementView/CashDepositView wrapper. Only the GET
  // single and LIST endpoints wrap as { disbursement: {...} } / { deposit: {...} } (confirmed by
  // CashDisbursementView/CashDepositView records and CashE2eTests.cs). So record*/void* below parse
  // the raw type directly, while get*/listCash unwrap the view.
  recordDisbursement(req: RecordCashVoucherRequest): Observable<CashDisbursement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDisbursement>(this.base('/cash-disbursements'), req);
  }
  recordDeposit(req: RecordCashVoucherRequest): Observable<CashDeposit> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDeposit>(this.base('/cash-deposits'), req);
  }
  getDisbursement(id: string): Observable<CashDisbursement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<CashDisbursementView>(this.base(`/cash-disbursements/${id}`)).pipe(map(v => v.disbursement));
  }
  getDeposit(id: string): Observable<CashDeposit> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<CashDepositView>(this.base(`/cash-deposits/${id}`)).pipe(map(v => v.deposit));
  }
  voidDisbursement(id: string, reason?: string | null): Observable<CashDisbursement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDisbursement>(this.base(`/cash-disbursements/${id}/void`), { reason: reason ?? null });
  }
  voidDeposit(id: string, reason?: string | null): Observable<CashDeposit> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<CashDeposit>(this.base(`/cash-deposits/${id}/void`), { reason: reason ?? null });
  }

  /** Combined cash list: fetch both kinds, normalize to signed rows (disbursement −, deposit +), sort by date desc. */
  listCash(q: BankingListQuery): Observable<PagedResponse<CashVoucherRow>> {
    if (!this.client.clientId()) return EMPTY;
    const params = this.listParams(q);
    return forkJoin({
      disb: this.http.get<PagedResponse<CashDisbursementView>>(this.base('/cash-disbursements'), { params }),
      dep: this.http.get<PagedResponse<CashDepositView>>(this.base('/cash-deposits'), { params }),
    }).pipe(map(({ disb, dep }) => {
      const rows: CashVoucherRow[] = [
        ...disb.items.map(v => ({ id: v.disbursement.id, kind: 'disbursement' as const, number: v.disbursement.number,
          date: v.disbursement.date, amount: this.sum(v.disbursement.lines), memo: v.disbursement.memo, status: v.disbursement.status })),
        ...dep.items.map(v => ({ id: v.deposit.id, kind: 'deposit' as const, number: v.deposit.number,
          date: v.deposit.date, amount: this.sum(v.deposit.lines), memo: v.deposit.memo, status: v.deposit.status })),
      ].sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : (a.number ?? '') < (b.number ?? '') ? 1 : -1));
      return { items: rows, total: disb.total + dep.total, skip: q.skip, limit: q.limit };
    }));
  }

  /** Posted journal entry(ies) for a banking document — powers the "posted journal entry" link. */
  entriesForSource(sourceRef: string): Observable<EntryResponse[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<EntryResponse[]>(this.base('/entries'), { params: new HttpParams().set('sourceRef', sourceRef) });
  }

  // ── Bank statements ──────────────────────────────────────────────────────────
  // NOTE: verified against Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/
  // ReconciliationEndpoints.cs — RecordStatement returns Results.Created(statement) (bare BankStatement),
  // ListStatements returns PagedResponse<BankStatement> (bare items), and ListStatements REQUIRES the
  // cashAccountId query parameter (400 if missing).
  listStatements(cashAccountId: string, q: BankingListQuery): Observable<PagedResponse<BankStatement>> {
    if (!this.client.clientId()) return EMPTY;
    const params = this.listParams(q).set('cashAccountId', cashAccountId);
    return this.http.get<PagedResponse<BankStatement>>(this.base('/bank-statements'), { params });
  }
  getStatement(id: string): Observable<BankStatement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<BankStatement>(this.base(`/bank-statements/${id}`));
  }
  recordStatement(req: RecordBankStatementRequest): Observable<BankStatement> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<BankStatement>(this.base('/bank-statements'), req);
  }
}
