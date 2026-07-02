import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { InvoiceEditor } from './invoice-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function route(id: string | null, customer: string | null = null) {
  return {
    provide: ActivatedRoute,
    useValue: {
      snapshot: {
        paramMap: { get: (_k: string) => id },
        queryParamMap: { get: (_k: string) => customer },
      },
    },
  };
}

describe('InvoiceEditor', () => {
  let ctrl: HttpTestingController;

  function setup(id: string | null, customer: string | null = null) {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        route(id, customer),
        provideCapabilities('ar.write'),
      ],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  }

  afterEach(() => ctrl.verify());

  it('new: validation gates save; live total; POSTs a draft then navigates', () => {
    setup(null, 'cu1');
    const f = TestBed.createComponent(InvoiceEditor);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    const cmp = f.componentInstance;
    expect(cmp.canSave()).toBe(false);                       // no lines / no amounts
    cmp.addLine();
    cmp.form.lines[0].description().value.set('Work');
    cmp.form.lines[0].quantity().value.set(2);
    cmp.form.lines[0].unitPrice().value.set(100);
    cmp.form.taxRate().value.set(0.07);
    f.detectChanges();
    expect(cmp.totals().subtotal).toBe(200);
    expect(cmp.totals().tax).toBe(14);
    expect(cmp.totals().total).toBe(214);
    expect(cmp.canSave()).toBe(true);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const post = ctrl.expectOne(r => r.method === 'POST' && r.url === 'http://localhost:5000/clients/C1/invoices');
    expect(post.request.body.customerId).toBe('cu1');
    expect(post.request.body.lines.length).toBe(1);
    expect(post.request.body.taxRate).toBe(0.07);
    post.flush({ id: 'inv1', customerId: 'cu1', number: null, issueDate: cmp.form.issueDate().value(), dueDate: null, status: 'Draft', taxRate: 0.07, memo: null, lines: post.request.body.lines });
    expect(nav).toHaveBeenCalledWith(['/receivables/invoices', 'inv1']);
  });

  it('edit: loads the draft (cold cache) and PUTs the same id', () => {
    setup('inv1');
    const f = TestBed.createComponent(InvoiceEditor);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
    f.detectChanges(); // let the effect observe customers loaded → calls getInvoice
    ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1').flush({
      invoice: { id: 'inv1', customerId: 'cu1', number: null, issueDate: '2026-06-29', dueDate: null, status: 'Draft', taxRate: 0.05, memo: null, lines: [{ description: 'A', quantity: 1, unitPrice: 50, taxable: true, revenueCategory: null }] },
      openBalance: 0,
      settlementStatus: 'Open',
    });
    f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.form.taxRate().value()).toBe(0.05);
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/invoices/inv1');
    expect(put.request.method).toBe('PUT');
    put.flush({ id: 'inv1', customerId: 'cu1', number: null, issueDate: '2026-06-29', dueDate: null, status: 'Draft', taxRate: 0.05, memo: null, lines: [] });
  });
});
