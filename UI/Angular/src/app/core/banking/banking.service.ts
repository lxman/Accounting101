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
  InterchangeFormat, CsvMapping, ImportPreviewResponse,
  ReconciliationRef, ReconciliationWorksheet, AutoMatchProposal,
  BankAdjustment, RecordAdjustmentRequest,
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

  /**
   * Combined cash list: fetch both kinds, normalize to signed rows (disbursement −, deposit +), sort by
   * date desc, then apply the CALLER's skip/limit to the merged set.
   *
   * NOTE: each stream is fetched at a fixed window (skip 0, limit 200 — the backend's max clamp) rather
   * than at the caller's own skip/limit, because the two streams must be merged BEFORE pagination is
   * applied (paginating each stream independently would let a single page render up to 2×limit rows and
   * would make total = disb.total + dep.total overcount when the two totals differ). If either stream's
   * own total exceeds 200, the combined list is truncated to what was fetched — acceptable for now, but
   * called out here rather than hidden.
   */
  listCash(q: BankingListQuery): Observable<PagedResponse<CashVoucherRow>> {
    if (!this.client.clientId()) return EMPTY;
    const fetchParams = this.listParams({ skip: 0, limit: 200, order: q.order });
    return forkJoin({
      disb: this.http.get<PagedResponse<CashDisbursementView>>(this.base('/cash-disbursements'), { params: fetchParams }),
      dep: this.http.get<PagedResponse<CashDepositView>>(this.base('/cash-deposits'), { params: fetchParams }),
    }).pipe(map(({ disb, dep }) => {
      const merged: CashVoucherRow[] = [
        ...disb.items.map(v => ({ id: v.disbursement.id, kind: 'disbursement' as const, number: v.disbursement.number,
          date: v.disbursement.date, amount: this.sum(v.disbursement.lines), memo: v.disbursement.memo, status: v.disbursement.status })),
        ...dep.items.map(v => ({ id: v.deposit.id, kind: 'deposit' as const, number: v.deposit.number,
          date: v.deposit.date, amount: this.sum(v.deposit.lines), memo: v.deposit.memo, status: v.deposit.status })),
      ].sort((a, b) => (a.date < b.date ? 1 : a.date > b.date ? -1 : (a.number ?? '') < (b.number ?? '') ? 1 : -1));
      const page = merged.slice(q.skip, q.skip + q.limit);
      return { items: page, total: merged.length, skip: q.skip, limit: q.limit };
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

  // ── Import (parse-to-preview) ────────────────────────────────────────────────
  importStatements(file: File, format: InterchangeFormat, mapping: CsvMapping | null): Observable<ImportPreviewResponse> {
    if (!this.client.clientId()) return EMPTY;
    const body = new FormData();
    body.append('file', file, file.name);
    body.append('format', format);
    if (mapping) body.append('mapping', JSON.stringify(mapping));
    return this.http.post<ImportPreviewResponse>(this.base('/bank-statements/import'), body);
  }

  // ── Reconciliation ───────────────────────────────────────────────────────────
  // NOTE: verified against Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/
  // ReconciliationEndpoints.cs — StartReconciliation returns Results.Created(reconciliation) (bare
  // ReconciliationRef); GetWorksheet/Clear/Unclear/Complete all return bare ReconciliationWorksheet;
  // AutoMatch returns AutoMatchProposal when apply is falsy/omitted and the worksheet when apply=true
  // (query param, not body).
  startReconciliation(bankStatementId: string): Observable<ReconciliationRef> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationRef>(this.base('/reconciliations'), { bankStatementId });
  }
  getWorksheet(id: string): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<ReconciliationWorksheet>(this.base(`/reconciliations/${id}`));
  }
  clear(id: string, entryIds: string[]): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/clear`), { entryIds });
  }
  unclear(id: string, entryIds: string[]): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/unclear`), { entryIds });
  }
  autoMatchProposal(id: string): Observable<AutoMatchProposal> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<AutoMatchProposal>(this.base(`/reconciliations/${id}/auto-match`), {}, { params: new HttpParams().set('apply', false) });
  }
  autoMatchApply(id: string): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/auto-match`), {}, { params: new HttpParams().set('apply', true) });
  }
  completeReconciliation(id: string): Observable<ReconciliationWorksheet> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<ReconciliationWorksheet>(this.base(`/reconciliations/${id}/complete`), {});
  }

  // ── Adjustments ──────────────────────────────────────────────────────────────
  // NOTE: verified against Modules/Banking/Reconciliation/Accounting101.Banking.Reconciliation.Api/
  // ReconciliationEndpoints.cs — all three routes nest under /reconciliations/{id}/adjustments;
  // RecordAdjustment returns Results.Created(adjustment) (bare BankAdjustment), ListAdjustments returns
  // PagedResponse<BankAdjustment> (bare items), VoidAdjustment returns Results.Ok(voided) (bare BankAdjustment).
  listAdjustments(reconciliationId: string, q: BankingListQuery): Observable<PagedResponse<BankAdjustment>> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<PagedResponse<BankAdjustment>>(this.base(`/reconciliations/${reconciliationId}/adjustments`), { params: this.listParams(q) });
  }
  recordAdjustment(reconciliationId: string, req: RecordAdjustmentRequest): Observable<BankAdjustment> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<BankAdjustment>(this.base(`/reconciliations/${reconciliationId}/adjustments`), req);
  }
  voidAdjustment(reconciliationId: string, adjId: string, reason?: string | null): Observable<BankAdjustment> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<BankAdjustment>(this.base(`/reconciliations/${reconciliationId}/adjustments/${adjId}/void`), { reason: reason ?? null });
  }
}
