import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ReceivablesService } from './receivables.service';
import { ClientContextService } from '../client/client-context.service';
import { Customer, DraftInvoiceRequest, InvoiceLine, invoiceTotals, lineAmount, autoAllocate, AllocRow, Payment, CreditDocument } from './receivables';

describe('pure math', () => {
  const makeLine = (quantity: number, unitPrice: number, taxable: boolean): InvoiceLine =>
    ({ description: 'x', quantity, unitPrice, taxable, revenueCategory: null });

  it('lineAmount multiplies quantity × unitPrice', () => {
    expect(lineAmount({ quantity: 2, unitPrice: 100 })).toBe(200);
  });

  it('invoiceTotals: taxableBase excludes non-taxable lines', () => {
    const lines = [makeLine(2, 100, true), makeLine(1, 50, false)];
    expect(invoiceTotals(lines, 0.07)).toEqual({ subtotal: 250, tax: 14, total: 264 });
  });

  it('invoiceTotals: zero tax rate yields tax=0', () => {
    expect(invoiceTotals([makeLine(1, 100, true)], 0)).toEqual({ subtotal: 100, tax: 0, total: 100 });
  });
});

describe('autoAllocate', () => {
  const row = (invoiceId: string, openBalance: number): AllocRow =>
    ({ invoiceId, number: invoiceId, issueDate: '2026-06-01', openBalance, allocation: 0 });

  it('fills oldest-first, capping each row at its open balance', () => {
    const out = autoAllocate(300, [row('a', 105), row('b', 150), row('c', 200)]);
    expect(out.map(r => r.allocation)).toEqual([105, 150, 45]);
  });

  it('partial first row when amount is less than the first open balance', () => {
    const out = autoAllocate(60, [row('a', 105), row('b', 150)]);
    expect(out.map(r => r.allocation)).toEqual([60, 0]);
  });

  it('excess over total open balances stays unallocated (rows capped)', () => {
    const out = autoAllocate(500, [row('a', 105), row('b', 150)]);
    expect(out.map(r => r.allocation)).toEqual([105, 150]);
    expect(out.reduce((s, r) => s + r.allocation, 0)).toBe(255);
  });

  it('zero amount allocates nothing', () => {
    const out = autoAllocate(0, [row('a', 105)]);
    expect(out.map(r => r.allocation)).toEqual([0]);
  });
});

describe('ReceivablesService', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('load() GETs customers and caches them; customerName resolves with id fallback', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.load();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush(
      [{ id: 'cu1', name: 'Acme Co', email: null }] as Customer[]);
    expect(svc.customers().length).toBe(1);
    expect(svc.customerName('cu1')).toBe('Acme Co');
    expect(svc.customerName('nope')).toBe('nope');           // fallback
    expect(svc.loadError()).toBeNull();
  });

  it('load() sets loadError when the GET fails', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.load();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush(
      { type: 'https://tools.ietf.org/html/rfc7807', title: 'Error', detail: 'Unauthorized', status: 401 },
      { status: 401, statusText: 'Unauthorized' },
    );
    expect(svc.loadError()).toBe('Unauthorized');
    expect(svc.customers().length).toBe(0);
  });

  it('create() POSTs and appends to the cache', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let made: Customer | undefined; svc.create('Beta LLC', 'b@x.com').subscribe(c => (made = c));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/customers');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Beta LLC', email: 'b@x.com' });
    req.flush({ id: 'cu2', name: 'Beta LLC', email: 'b@x.com' } as Customer);
    expect(made!.id).toBe('cu2'); expect(svc.customers().some(c => c.id === 'cu2')).toBe(true);
  });

  it('listInvoices() builds the query string', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.listInvoices({ customerId: 'cu1', settlement: 'open', skip: 0, limit: 50, order: 'desc' }).subscribe();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/invoices'
      && r.params.get('customerId') === 'cu1' && r.params.get('settlement') === 'open'
      && r.params.get('skip') === '0' && r.params.get('limit') === '50' && r.params.get('order') === 'desc');
    req.flush({ items: [], total: 0, skip: 0, limit: 50 });
    expect(req.request.method).toBe('GET');
  });

  it('draft() returns EMPTY (no HTTP) when no client is selected', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    // no ClientContextService.select() → clientId() stays null
    const req: DraftInvoiceRequest = { customerId: 'cu1', lines: [], taxRate: 0, issueDate: '2026-06-29', dueDate: null, memo: null };
    let emitted = false;
    svc.draft(req).subscribe({ next: () => (emitted = true) });
    ctrl.verify(); // no requests expected
    expect(emitted).toBe(false);
  });

  it('draft/issue/void hit the right method, URL and body', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const req: DraftInvoiceRequest = { customerId: 'cu1', lines: [{ description: 'Work', quantity: 1, unitPrice: 100, taxable: true, revenueCategory: null }], taxRate: 0.07, issueDate: '2026-06-29', dueDate: null, memo: null };
    svc.draft(req).subscribe();
    const post = ctrl.expectOne('http://localhost:5000/clients/C1/invoices');
    expect(post.request.method).toBe('POST'); expect(post.request.body).toEqual(req);
    post.flush({ id: 'inv1', customerId: 'cu1', number: null, issueDate: '2026-06-29', dueDate: null, status: 'Draft', taxRate: 0.07, memo: null, lines: req.lines });

    svc.issue('inv1').subscribe();
    const issue = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/issue');
    expect(issue.request.method).toBe('POST'); issue.flush({ id: 'inv1', customerId: 'cu1', number: '1001', issueDate: '2026-06-29', dueDate: null, status: 'Issued', taxRate: 0.07, memo: null, lines: req.lines });

    svc.void('inv1', 'mistake').subscribe();
    const v = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/void');
    expect(v.request.method).toBe('POST'); expect(v.request.body).toEqual({ reason: 'mistake' });
    v.flush({ id: 'inv1', customerId: 'cu1', number: '1001', issueDate: '2026-06-29', dueDate: null, status: 'Void', taxRate: 0.07, memo: null, lines: req.lines });
  });

  it('listPayments GETs /payments?customerId=', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let result: Payment[] | undefined;
    svc.listPayments('cu1').subscribe(p => (result = p));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/payments' && r.params.get('customerId') === 'cu1');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 60, method: 'check', allocations: [{ targetId: 'inv1', amount: 60 }], voided: false }] as Payment[]);
    expect(result!.length).toBe(1);
  });

  it('recordPayment POSTs the request to /payments', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.recordPayment({ customerId: 'cu1', date: '2026-06-30', amount: 60, method: null, allocations: [{ targetId: 'inv1', amount: 60 }] }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payments');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.allocations).toEqual([{ targetId: 'inv1', amount: 60 }]);
    req.flush({ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 60, method: null, allocations: [{ targetId: 'inv1', amount: 60 }], voided: false });
  });

  it('voidPayment POSTs the reason to /payments/{id}/void', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.voidPayment('p1', 'oops').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payments/p1/void');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'oops' });
    req.flush({ id: 'p1', customerId: 'cu1', date: '2026-06-30', amount: 60, method: null, allocations: [], voided: true });
  });

  it('creditBalance GETs and unwraps creditBalance', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let bal: number | undefined;
    svc.creditBalance('cu1').subscribe(b => (bal = b));
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance')
      .flush({ customerId: 'cu1', creditBalance: 42.5 });
    expect(bal).toBe(42.5);
  });

  it('listCredits GETs /credits?customerId=', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let result: unknown[] | undefined;
    svc.listCredits('cu1').subscribe(c => (result = c));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/credits' && r.params.get('customerId') === 'cu1');
    expect(req.request.method).toBe('GET');
    req.flush([{ type: 'credit-note', id: 'cn1', customerId: 'cu1', date: '2026-06-30', amount: 100, memo: 'x', allocations: [{ targetId: 'inv1', amount: 100 }], voided: false }]);
    expect(result!.length).toBe(1);
  });

  it('recordCreditNote POSTs to /credit-notes', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.recordCreditNote({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 100 }], memo: 'returned' }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 100 }], memo: 'returned' });
    req.flush({});
  });

  it('recordWriteOff POSTs to /write-offs', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.recordWriteOff({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 100 }], memo: 'bad debt' }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/write-offs');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.memo).toBe('bad debt');
    req.flush({});
  });

  it('applyCredit POSTs to /credit-applications (no memo field)', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.applyCredit({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 50 }] }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/credit-applications');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: '2026-06-30', allocations: [{ targetId: 'inv1', amount: 50 }] });
    req.flush({});
  });

  it('voidCredit maps type to the right path (credit-note / write-off)', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.voidCredit('credit-note', 'cn1', 'oops').subscribe();
    const a = ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes/cn1/void');
    expect(a.request.method).toBe('POST'); expect(a.request.body).toEqual({ reason: 'oops' }); a.flush({});
    svc.voidCredit('write-off', 'wo1').subscribe();
    const b = ctrl.expectOne('http://localhost:5000/clients/C1/write-offs/wo1/void');
    expect(b.request.method).toBe('POST'); expect(b.request.body).toEqual({ reason: null }); b.flush({});
  });

  it('listRefunds GETs /refunds?customerId=', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let result: unknown[] | undefined;
    svc.listRefunds('cu1').subscribe(r => (result = r));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/refunds' && r.params.get('customerId') === 'cu1');
    expect(req.request.method).toBe('GET');
    req.flush([{ id: 'rf1', customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'x', voided: false }]);
    expect(result!.length).toBe(1);
  });

  it('recordRefund POSTs to /refunds', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.recordRefund({ customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'overpay' }).subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/refunds');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: '2026-06-30', amount: 50, memo: 'overpay' });
    req.flush({});
  });

  it('voidRefund POSTs the reason to /refunds/{id}/void', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    svc.voidRefund('rf1', 'oops').subscribe();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/refunds/rf1/void');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'oops' });
    req.flush({});
  });

  it('getCustomerAccount GETs /customers/{id}/account', () => {
    const svc = TestBed.inject(ReceivablesService); const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    let result: { arBalance: number } | undefined;
    svc.getCustomerAccount('cu1').subscribe(v => (result = v));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/account');
    expect(req.request.method).toBe('GET');
    req.flush({
      customer: { id: 'cu1', name: 'Acme Co', email: null }, arBalance: 1900, creditBalance: 50,
      aging: { current: 0, d1to30: 0, d31to60: 0, d61to90: 0, d90plus: 1900 },
      openInvoices: [], statementLines: [], creditLines: [],
    });
    expect(result!.arBalance).toBe(1900);
  });
});
