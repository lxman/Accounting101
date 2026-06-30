import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { of } from 'rxjs';
import { AdjustmentEditor } from './adjustment-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function setup(customer: string | null) {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => (k === 'customer' ? customer : null) } } } },
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const invoicesPage = (rows: { id: string; number: string; open: number }[]) => ({
  items: rows.map(r => ({
    invoice: { id: r.id, customerId: 'cu1', number: r.number, issueDate: '2026-03-01', dueDate: null, status: 'Issued', taxRate: 0, memo: null, lines: [] },
    openBalance: r.open, settlementStatus: 'Open',
  })),
  total: rows.length, skip: 0, limit: 200,
});

function loadInvoices(ctrl: HttpTestingController, f: any, rows: { id: string; number: string; open: number }[], credit = 0) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices')).flush(invoicesPage(rows));
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/customers/cu1/credit-balance')).flush({ customerId: 'cu1', creditBalance: credit });
  f.detectChanges();
}

describe('AdjustmentEditor', () => {
  it('redirects to /receivables/credits when reached without a customer', () => {
    setup(null);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    expect(nav).toHaveBeenCalledWith(['/receivables/credits']);
  });

  it('ticking a row fills its open balance and counts toward total; unticking clears it', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }]);
    const c = f.componentInstance;
    c.toggleRow(0, true); f.detectChanges();
    expect(c.rows()[0].amount).toBe(100);
    expect(c.total()).toBe(100);
    c.toggleRow(0, false); f.detectChanges();
    expect(c.rows()[0].amount).toBe(0);
    expect(c.total()).toBe(0);
  });

  it('caps an included row amount at its open balance (invalid above)', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }]);
    const c = f.componentInstance;
    c.toggleRow(0, true); c.setAmount(0, 150); f.detectChanges();
    expect(c.valid()).toBe(false);
  });

  it('hides memo for apply-credit and shows it otherwise; caps total at available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }], 40);
    const c = f.componentInstance;
    c.setType('credit-application'); f.detectChanges();
    expect(f.nativeElement.textContent).not.toContain('Memo');
    expect(f.nativeElement.textContent).toContain('Available credit');
    c.toggleRow(0, true); f.detectChanges();           // amount 100 > 40 credit
    expect(c.valid()).toBe(false);
    c.setAmount(0, 40); f.detectChanges();
    expect(c.valid()).toBe(true);
  });

  it('submits the correct payload to the correct endpoint per type', () => {
    for (const [type, segment] of [['credit-note', 'credit-notes'], ['write-off', 'write-offs'], ['credit-application', 'credit-applications']] as const) {
      const ctrl = setup('cu1');
      const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
      loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }], 100);
      const c = f.componentInstance;
      c.setType(type); c.toggleRow(0, true); f.detectChanges();
      c.save();
      const req = ctrl.expectOne(`http://localhost:5000/clients/C1/${segment}`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body.allocations).toEqual([{ targetId: 'inv1', amount: 100 }]);
      if (type === 'credit-application') expect('memo' in req.request.body).toBe(false);
      req.flush({});
      TestBed.resetTestingModule();
    }
  });

  it('relays a 422 error inline', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(AdjustmentEditor); f.detectChanges();
    loadInvoices(ctrl, f, [{ id: 'inv1', number: '1001', open: 100 }]);
    const c = f.componentInstance;
    c.toggleRow(0, true); f.detectChanges();
    c.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/credit-notes').flush(
      { type: 'about:blank', title: 'Unprocessable', detail: 'Allocation exceeds open balance.', status: 422 },
      { status: 422, statusText: 'Unprocessable Entity' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Allocation exceeds open balance.');
  });
});
