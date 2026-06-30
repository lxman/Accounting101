import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { InvoiceList } from './invoice-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { InvoiceView } from '../../core/receivables/receivables';

function inv(id: string, number: string | null, status: 'Draft' | 'Issued', open = 0): InvoiceView {
  return {
    invoice: { id, customerId: 'cu1', number, issueDate: '2026-06-29', dueDate: null, status, taxRate: 0, memo: null, lines: [] },
    openBalance: open,
    settlementStatus: open > 0 ? 'Open' : 'Paid',
  };
}

describe('InvoiceList', () => {
  beforeEach(() => {
    localStorage.clear();   // isolate the per-client persisted customer selection between tests
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    TestBed.inject(ClientContextService).select('C1');
  });

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('selecting a customer loads their invoices; clicking row navigates to detail', () => {
    const f = TestBed.createComponent(InvoiceList); f.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    const req = ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('customerId') === 'cu1');
    req.flush({ items: [inv('inv1', '1001', 'Issued', 500)], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('1001'); expect(text).toContain('500.00');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr');
    row.click();
    expect(nav).toHaveBeenCalledWith(['/receivables/invoices', 'inv1']);
  });

  it('retains the selected customer when the component is left and re-created', () => {
    const ctrl = TestBed.inject(HttpTestingController);
    // First visit: pick a customer and load their invoices.
    const f1 = TestBed.createComponent(InvoiceList); f1.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f1.detectChanges();
    f1.componentInstance.svc.setSelectedCustomer('cu1'); f1.detectChanges();
    ctrl.expectOne(r => r.params.get('customerId') === 'cu1').flush({ items: [], total: 0, skip: 0, limit: 50 });
    expect(localStorage.getItem('a101.recv.customer.C1')).toBe('cu1');   // persisted
    f1.destroy();

    // Come back: selection is restored, invoices auto-load without re-selecting.
    const f2 = TestBed.createComponent(InvoiceList); f2.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f2.detectChanges();
    expect(f2.componentInstance.customerId()).toBe('cu1');
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('customerId') === 'cu1')
      .flush({ items: [inv('inv1', '1001', 'Issued', 500)], total: 1, skip: 0, limit: 50 });
    f2.detectChanges();
    expect(f2.nativeElement.textContent).toContain('1001');
  });

  it('clears a restored selection if that customer no longer exists (load guard)', () => {
    const svc = TestBed.inject(ReceivablesService);
    const ctrl = TestBed.inject(HttpTestingController);
    svc.setSelectedCustomer('gone');                      // a selection that will not be in the list
    expect(localStorage.getItem('a101.recv.customer.C1')).toBe('gone');
    svc.load();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    expect(svc.selectedCustomerId()).toBe('');
    expect(localStorage.getItem('a101.recv.customer.C1')).toBeNull();
  });

  it('shows no-customers empty state when customer list is empty', () => {
    const f = TestBed.createComponent(InvoiceList); f.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('http://localhost:5000/clients/C1/customers').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No customers yet');
  });

  it('shows a prompt when customers exist but none is selected', () => {
    const f = TestBed.createComponent(InvoiceList); f.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('http://localhost:5000/clients/C1/customers')
      .flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Select a customer');
  });

  it('sets listError when listInvoices fails', () => {
    const f = TestBed.createComponent(InvoiceList); f.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    f.componentInstance.svc.setSelectedCustomer('cu1'); f.detectChanges();
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices')).flush(
      { type: 'https://tools.ietf.org/html/rfc7807', title: 'Error', detail: 'Forbidden', status: 403 },
      { status: 403, statusText: 'Forbidden' },
    );
    f.detectChanges();
    expect(f.componentInstance.listError()).toBe('Forbidden');
    expect(f.nativeElement.textContent).toContain('Forbidden');
  });

  it('has no Record-payment control in the invoice-list header (moved to the Payments tab)', () => {
    const ctrl = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(InvoiceList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges();
    const recordLink = [...f.nativeElement.querySelectorAll('a')].find(a => a.textContent.trim() === 'Record payment');
    expect(recordLink).toBeFalsy();
  });
});
