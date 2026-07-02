import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RefundEditor } from './refund-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(customer: string | null) {
  localStorage.clear();
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: { get: (k: string) => (k === 'customer' ? customer : null) } } } },
      provideCapabilities('ar.write'),
    ],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

function loadCreditBalance(ctrl: HttpTestingController, f: any, balance: number) {
  ctrl.expectOne('http://localhost:5000/clients/C1/customers').flush([{ id: 'cu1', name: 'Acme Co', email: null }]);
  ctrl.expectOne(r => r.url.endsWith('/clients/C1/customers/cu1/credit-balance')).flush({ customerId: 'cu1', creditBalance: balance });
  f.detectChanges();
}

describe('RefundEditor', () => {
  it('redirects to /receivables/refunds when reached without a customer', () => {
    setup(null);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    expect(nav).toHaveBeenCalledWith(['/receivables/refunds']);
  });

  it('defaults the amount to the loaded available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    expect(f.componentInstance.amount()).toBe(75);
    expect(f.componentInstance.valid()).toBe(true);
  });

  it('is invalid when the amount exceeds available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    f.componentInstance.amount.set(100); f.detectChanges();
    expect(f.componentInstance.valid()).toBe(false);
  });

  it('is invalid when there is no available credit', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 0);
    expect(f.componentInstance.amount()).toBe(0);
    expect(f.componentInstance.valid()).toBe(false);
  });

  it('submits the refund payload and navigates to the refunds list', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    f.componentInstance.amount.set(50); f.componentInstance.memo.set('overpay'); f.detectChanges();
    f.componentInstance.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/refunds');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ customerId: 'cu1', date: f.componentInstance.date(), amount: 50, memo: 'overpay' });
    req.flush({});
    expect(nav).toHaveBeenCalledWith(['/receivables/refunds']);
  });

  it('relays a 422 error inline', () => {
    const ctrl = setup('cu1');
    const f = TestBed.createComponent(RefundEditor); f.detectChanges();
    loadCreditBalance(ctrl, f, 75);
    f.componentInstance.amount.set(50); f.detectChanges();
    f.componentInstance.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/refunds').flush(
      { type: 'about:blank', title: 'Unprocessable', detail: 'Refund of 50 exceeds available credit 40.', status: 422 },
      { status: 422, statusText: 'Unprocessable Entity' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('exceeds available credit');
  });
});
