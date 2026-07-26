import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CashVoucherEditor } from './cash-voucher-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { vi } from 'vitest';

function make(kind: 'disbursement' | 'deposit') {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('cash.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { data: { kind } } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  const fixture = TestBed.createComponent(CashVoucherEditor);
  fixture.detectChanges();
  const ctrl = TestBed.inject(HttpTestingController);
  ctrl.expectOne(r => r.url.endsWith('/accounts')).flush(
    [{ id: 'a1', number: '6200', name: 'Rent', type: 'Expense', postable: true }]);   // bare array — accounts endpoint is not paged
  fixture.detectChanges();
  return { fixture, ctrl };
}

describe('CashVoucherEditor', () => {
  it('posts a disbursement to the disbursements endpoint', () => {
    const { fixture, ctrl } = make('disbursement');
    const cmp = fixture.componentInstance;
    cmp.setAccount(0, 'a1'); cmp.setAmount(0, 500);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    expect(req.request.body.lines).toEqual([{ accountId: 'a1', amount: 500 }]);
    req.flush({ id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 500 }],
      date: cmp.date(), reference: null, memo: null, status: 'Posted' });
    ctrl.verify();
  });

  it('sends a retry key with the disbursement', () => {
    const { fixture, ctrl } = make('disbursement');
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const cmp = fixture.componentInstance;
    cmp.setAccount(0, 'a1'); cmp.setAmount(0, 500);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    expect(req.request.body.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
    req.flush({ id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 500 }],
      date: cmp.date(), reference: null, memo: null, status: 'Posted' });
    ctrl.verify();
  });

  it('reuses the same key when a failed save is retried', () => {
    const { fixture, ctrl } = make('disbursement');
    const cmp = fixture.componentInstance;
    cmp.setAccount(0, 'a1'); cmp.setAmount(0, 500);
    cmp.save();
    const post1 = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    const key1 = post1.request.body.id;
    post1.flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    cmp.save();
    const post2 = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    expect(post2.request.body.id).toBe(key1);
    post2.flush({ detail: 'boom' }, { status: 500, statusText: 'Server Error' });
    ctrl.verify();
  });

  it('mints a fresh key after a successful save', () => {
    const { fixture, ctrl } = make('disbursement');
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const cmp = fixture.componentInstance;
    cmp.setAccount(0, 'a1'); cmp.setAmount(0, 500);
    cmp.save();
    const post1 = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    const key1 = post1.request.body.id;
    post1.flush({ id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a1', amount: 500 }],
      date: cmp.date(), reference: null, memo: null, status: 'Posted' });
    cmp.save();
    const post2 = ctrl.expectOne('http://localhost:5000/clients/C1/cash-disbursements');
    expect(post2.request.body.id).not.toBe(key1);
    post2.flush({ id: 'v2', number: 'CD-00002', lines: [{ accountId: 'a1', amount: 500 }],
      date: cmp.date(), reference: null, memo: null, status: 'Posted' });
    ctrl.verify();
  });

  it('sends a retry key on the deposit branch too', () => {
    const { fixture, ctrl } = make('deposit');
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const cmp = fixture.componentInstance;
    cmp.setAccount(0, 'a1'); cmp.setAmount(0, 500);
    cmp.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/cash-deposits');
    expect(req.request.body.id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
    req.flush({ id: 'v1', number: 'CR-00001', lines: [{ accountId: 'a1', amount: 500 }],
      date: cmp.date(), reference: null, memo: null, status: 'Posted' });
    ctrl.verify();
  });
});
