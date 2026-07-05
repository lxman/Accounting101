import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CashList } from './cash-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

describe('CashList', () => {
  it('renders a row per voucher with signed amounts', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('cash.write')],
    });
    TestBed.inject(ClientContextService).select('C1');
    const fixture = TestBed.createComponent(CashList);
    fixture.detectChanges();
    const ctrl = TestBed.inject(HttpTestingController);
    ctrl.expectOne(r => r.url.endsWith('/cash-disbursements')).flush(
      { items: [{ disbursement: { id: 'v1', number: 'CD-00001', lines: [{ accountId: 'a', amount: 50 }],
        date: '2026-03-01', reference: null, memo: 'rent', status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    ctrl.expectOne(r => r.url.endsWith('/cash-deposits')).flush({ items: [], total: 0, skip: 0, limit: 50 });
    fixture.detectChanges();
    const rows = fixture.nativeElement.querySelectorAll('tbody tr');
    expect(rows.length).toBe(1);
    expect(rows[0].textContent).toContain('CD-00001');
    ctrl.verify();
  });
});
