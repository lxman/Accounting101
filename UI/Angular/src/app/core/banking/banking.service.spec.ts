import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BankingService } from './banking.service';
import { ClientContextService } from '../client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(BankingService), ctrl: TestBed.inject(HttpTestingController) };
}

describe('BankingService — cash', () => {
  it('recordDisbursement posts and returns the raw disbursement', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.recordDisbursement({ lines: [{ accountId: 'a1', amount: 50 }], date: '2026-03-01' }).subscribe(d => (got = d));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    expect(req.request.method).toBe('POST');
    // NOTE: verified against Modules/Banking/Cash/Accounting101.Banking.Cash.Api/CashEndpoints.cs —
    // RecordDisbursement returns Results.Created(disbursement) — the RAW CashDisbursement, not a
    // CashDisbursementView wrapper. Only the GET single/list endpoints wrap as { disbursement: {...} }.
    req.flush({ id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 50 }],
      date: '2026-03-01', reference: null, memo: null, status: 'Posted' });
    expect(got.id).toBe('v1');
    ctrl.verify();
  });

  it('listCash normalizes disbursements and deposits into signed rows', () => {
    const { svc, ctrl } = setup();
    let rows: { kind: string; amount: number }[] = [];
    svc.listCash({ skip: 0, limit: 50 }).subscribe(p => (rows = p.items));
    const d = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/cash-disbursements');
    d.flush({ items: [{ disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a', amount: 50 }],
      date: '2026-03-01', reference: null, memo: null, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    const p = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/cash-deposits');
    p.flush({ items: [{ deposit: { id: 'w1', number: 'CR-00001', lines: [{ accountId: 'b', amount: 30 }],
      date: '2026-03-02', reference: null, memo: null, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    expect(rows.find(r => r.kind === 'disbursement')!.amount).toBe(50);
    expect(rows.find(r => r.kind === 'deposit')!.amount).toBe(30);
    ctrl.verify();
  });

  it('voidDeposit posts a reason and returns the raw deposit', () => {
    const { svc, ctrl } = setup();
    svc.voidDeposit('w1', 'error').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-deposits/w1/void');
    expect(req.request.body).toEqual({ reason: 'error' });
    // NOTE: VoidDeposit returns Results.Ok(voided) — the RAW CashDeposit, not a CashDepositView wrapper.
    req.flush({ id: 'w1', number: 'CR-00001', lines: [], date: '2026-03-02',
      reference: null, memo: null, status: 'Void' });
    ctrl.verify();
  });

  it('getDisbursement unwraps the wrapped view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.getDisbursement('v1').subscribe(d => (got = d));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements/v1');
    expect(req.request.method).toBe('GET');
    req.flush({ disbursement: { id: 'v1', number: 'CD-00001', lines: [], date: '2026-03-01',
      reference: null, memo: null, status: 'Posted' } });
    expect(got.id).toBe('v1');
    ctrl.verify();
  });

  it('getDeposit unwraps the wrapped view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.getDeposit('w1').subscribe(d => (got = d));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-deposits/w1');
    expect(req.request.method).toBe('GET');
    req.flush({ deposit: { id: 'w1', number: 'CR-00001', lines: [], date: '2026-03-02',
      reference: null, memo: null, status: 'Posted' } });
    expect(got.id).toBe('w1');
    ctrl.verify();
  });

  it('entriesForSource sets the sourceRef param', () => {
    const { svc, ctrl } = setup();
    svc.entriesForSource('d1').subscribe();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries');
    expect(req.request.params.get('sourceRef')).toBe('d1');
    req.flush([]);
    ctrl.verify();
  });
});
