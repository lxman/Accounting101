import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { RemittanceEditor } from './remittance-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('payroll.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RemittanceEditor', () => {
  it('shows the total and posts the remittance, navigating to detail', () => {
    const ctrl = setup();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(RemittanceEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.withholdingsAmount.set(170); cmp.taxesAmount.set(153); cmp.payDate.set('2026-06-30'); cmp.memo.set('Q2');
    f.detectChanges();
    expect(cmp.total()).toBe(323);
    expect(cmp.canSave()).toBe(true);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances');
    expect(req.request.body).toEqual({ withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: 'Q2', id: expect.any(String) });
    req.flush({ id: 'm9', number: 'TR-9', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: 'Q2', status: 'Posted' });
    expect(nav).toHaveBeenCalledWith(['/payroll/remittances', 'm9']);
    ctrl.verify();
  });

  it('sends a retry key with the remittance', () => {
    const ctrl = setup();
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(RemittanceEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.withholdingsAmount.set(170); cmp.taxesAmount.set(153); cmp.payDate.set('2026-06-30');
    f.detectChanges();
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances');
    expect(req.request.body.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
    req.flush({ id: 'm9', number: 'TR-9', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: null, status: 'Posted' });
    ctrl.verify();
  });

  it('reuses the same key when a failed save is retried', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RemittanceEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.withholdingsAmount.set(170); cmp.taxesAmount.set(153); cmp.payDate.set('2026-06-30');
    f.detectChanges();
    cmp.save();
    const req1 = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances');
    const key1 = req1.request.body.id;
    req1.flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    cmp.save();
    const req2 = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances');
    expect(req2.request.body.id).toBe(key1);
    req2.flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    ctrl.verify();
  });

  it('mints a fresh key after a successful save', () => {
    const ctrl = setup();
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const f = TestBed.createComponent(RemittanceEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.withholdingsAmount.set(170); cmp.taxesAmount.set(153); cmp.payDate.set('2026-06-30');
    f.detectChanges();
    cmp.save();
    const req1 = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances');
    const key1 = req1.request.body.id;
    req1.flush({ id: 'm9', number: 'TR-9', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: null, status: 'Posted' });
    cmp.save();
    const req2 = ctrl.expectOne('http://localhost:5000/clients/C1/tax-remittances');
    expect(req2.request.body.id).not.toBe(key1);
    req2.flush({ id: 'm10', number: 'TR-10', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: null, status: 'Posted' });
    ctrl.verify();
  });
});
