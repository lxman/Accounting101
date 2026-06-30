import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ReceivablesService } from './receivables.service';
import { ClientContextService } from '../client/client-context.service';
import { Customer, DraftInvoiceRequest } from './receivables';

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
});
