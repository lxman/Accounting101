import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CashVoucherDetail } from './cash-voucher-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

describe('CashVoucherDetail', () => {
  it('loads a disbursement and shows its number', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('cash.write'),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'v1']]) } } }],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(CashVoucherDetail);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/cash-disbursements/v1')).flush(
      { disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a', amount: 50 }],
        date: '2026-03-01', reference: null, memo: null, status: 'Posted' } });
    ctrl.expectOne(r => r.url.endsWith('/entries')).flush([]);
    ctrl.expectOne(r => r.url.endsWith('/accounts')).flush([{ id: 'a', number: '6200', name: 'Rent', type: 'Expense', postable: true }]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('CD-00001');
    ctrl.verify();
  });
});
