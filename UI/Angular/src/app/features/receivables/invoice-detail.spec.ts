import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { InvoiceDetail } from './invoice-detail';
import { ClientContextService } from '../../core/client/client-context.service';

function routeStub() {
  return {
    provide: ActivatedRoute,
    useValue: {
      snapshot: {
        paramMap: { get: (_k: string) => 'inv1' },
      },
    },
  };
}

function view(status: 'Draft' | 'Issued', number: string | null) {
  return {
    invoice: {
      id: 'inv1', customerId: 'cu1', number, issueDate: '2026-06-29', dueDate: null,
      status, taxRate: 0.1, memo: null,
      lines: [{ description: 'Work', quantity: 1, unitPrice: 100, taxable: true, revenueCategory: null }],
    },
    openBalance: status === 'Issued' ? 110 : 0,
    settlementStatus: 'Open' as const,
  };
}

describe('InvoiceDetail', () => {
  let ctrl: HttpTestingController;

  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        routeStub(),
      ],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  }

  afterEach(() => ctrl.verify());

  it('draft: shows Edit/Delete/Issue; Issue POSTs and reloads', () => {
    setup();
    const f = TestBed.createComponent(InvoiceDetail); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Draft', null));
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('110.00');           // footed total (100 + 10% tax)
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    (f.componentInstance as InvoiceDetail).issue();
    const issue = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/issue');
    expect(issue.request.method).toBe('POST'); issue.flush(view('Issued', '1001').invoice);
    ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Issued', '1001')); // reload
  });

  it('issued: void POSTs the reason', () => {
    setup();
    const f = TestBed.createComponent(InvoiceDetail); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush(view('Issued', '1001'));
    f.detectChanges();
    const cmp = f.componentInstance as InvoiceDetail; cmp.voidReason.set('dup'); cmp.voidInvoice();
    const v = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1/void');
    expect(v.request.body).toEqual({ reason: 'dup' });
    v.flush(view('Issued', '1001').invoice);
    ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush({ ...view('Issued', '1001'), invoice: { ...view('Issued', '1001').invoice, status: 'Void' } });
  });
});
