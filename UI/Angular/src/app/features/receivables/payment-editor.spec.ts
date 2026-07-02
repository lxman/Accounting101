import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PaymentEditor } from './payment-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function routeStub(params: Record<string, string | null>) {
  return { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => params[k] ?? null } } } };
}

function openInvoice(id: string, number: string, open: number) {
  return {
    invoice: { id, customerId: 'cu1', number, issueDate: '2026-06-01', dueDate: null, status: 'Issued', taxRate: 0, memo: null, lines: [] },
    openBalance: open, settlementStatus: 'Open' as const,
  };
}

function setup(params: Record<string, string | null>) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), routeStub(params), provideCapabilities('ar.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  const ctrl = TestBed.inject(HttpTestingController);
  return ctrl;
}

describe('PaymentEditor', () => {
  it('redirects to /receivables when no customer query param', () => {
    const ctrl = setup({ customer: null });
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    TestBed.createComponent(PaymentEditor).detectChanges();
    expect(nav).toHaveBeenCalledWith(['/receivables']);
    ctrl.verify();
  });

  it('loads open invoices, auto-allocates the entered amount oldest-first, and posts the payment', () => {
    const ctrl = setup({ customer: 'cu1' });
    const f = TestBed.createComponent(PaymentEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme', email: null }]);
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('settlement') === 'open')
      .flush({ items: [openInvoice('inv1', '1001', 105), openInvoice('inv2', '1002', 150)], total: 2, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance').flush({ customerId: 'cu1', creditBalance: 0 });
    f.detectChanges();
    const cmp = f.componentInstance as PaymentEditor;
    cmp.onAmount(200); f.detectChanges();
    expect(cmp.rows().map(r => r.allocation)).toEqual([105, 95]);
    expect(cmp.unallocated()).toBe(0);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/payments');
    expect(req.request.body.amount).toBe(200);
    expect(req.request.body.allocations).toEqual([{ targetId: 'inv1', amount: 105 }, { targetId: 'inv2', amount: 95 }]);
    req.flush({ id: 'p1', customerId: 'cu1', date: cmp.date(), amount: 200, method: null, allocations: req.request.body.allocations, voided: false });
    expect(nav).toHaveBeenCalledWith(['/receivables']);
    ctrl.verify();
  });

  it('prefills the amount from the focused invoice and reports overpayment as credit', () => {
    const ctrl = setup({ customer: 'cu1', invoice: 'inv2' });
    const f = TestBed.createComponent(PaymentEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme', email: null }]);
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices')).flush(
      { items: [openInvoice('inv1', '1001', 105), openInvoice('inv2', '1002', 150)], total: 2, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance').flush({ customerId: 'cu1', creditBalance: 0 });
    f.detectChanges();
    const cmp = f.componentInstance as PaymentEditor;
    // focused invoice sorted first, amount prefilled to its open balance
    expect(cmp.rows()[0].invoiceId).toBe('inv2');
    expect(cmp.amount()).toBe(150);
    // open balances total 255 (150 + 105); pay 305 → all allocated, 50 left as credit
    cmp.onAmount(305); f.detectChanges();
    expect(cmp.rows().map(r => r.allocation)).toEqual([150, 105]);
    expect(cmp.unallocated()).toBe(50);                // → customer credit
    expect(cmp.valid()).toBe(true);
    ctrl.verify();
  });

  it('shows the existing customer credit when the customer has one', () => {
    const ctrl = setup({ customer: 'cu1' });
    const f = TestBed.createComponent(PaymentEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme', email: null }]);
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('settlement') === 'open')
      .flush({ items: [], total: 0, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance').flush({ customerId: 'cu1', creditBalance: 25 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Existing customer credit');
    expect(f.nativeElement.textContent).toContain('25.00');
    ctrl.verify();
  });

  it('warns and disables Save when allocations exceed the payment amount', () => {
    const ctrl = setup({ customer: 'cu1' });
    const f = TestBed.createComponent(PaymentEditor); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme', email: null }]);
    ctrl.expectOne(r => r.url.endsWith('/clients/C1/invoices') && r.params.get('settlement') === 'open')
      .flush({ items: [openInvoice('inv1', '1001', 105)], total: 1, skip: 0, limit: 200 });
    ctrl.expectOne('http://localhost:5000/clients/C1/customers/cu1/credit-balance').flush({ customerId: 'cu1', creditBalance: 0 });
    f.detectChanges();
    const cmp = f.componentInstance as PaymentEditor;
    cmp.onAmount(50); cmp.onRow(0, 80); f.detectChanges();   // allocated 80 > amount 50 (row 80 <= open 105)
    expect(cmp.valid()).toBe(false);
    expect(cmp.allocationWarning()).toContain('exceeds the payment amount');
    expect(f.nativeElement.textContent).toContain('exceeds the payment amount');
    cmp.onRow(0, 40); f.detectChanges();                      // 40 <= amount 50 → valid
    expect(cmp.valid()).toBe(true);
    expect(cmp.allocationWarning()).toBeNull();
    ctrl.verify();
  });
});
