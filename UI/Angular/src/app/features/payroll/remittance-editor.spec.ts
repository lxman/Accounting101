import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { RemittanceEditor } from './remittance-editor';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
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
    expect(req.request.body).toEqual({ withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: 'Q2' });
    req.flush({ id: 'm9', number: 'TR-9', withholdingsAmount: 170, taxesAmount: 153, payDate: '2026-06-30', memo: 'Q2', status: 'Posted' });
    expect(nav).toHaveBeenCalledWith(['/payroll/remittances', 'm9']);
    ctrl.verify();
  });
});
