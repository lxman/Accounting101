import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RemittanceList } from './remittance-list';
import { ClientContextService } from '../../core/client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('RemittanceList', () => {
  it('renders remittances with computed total', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RemittanceList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/tax-remittances')
      .flush({ items: [{ remittance: { id: 'm1', number: 'TR-1', withholdingsAmount: 170, taxesAmount: 153,
        payDate: '2026-06-30', memo: null, status: 'Posted' } }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('TR-1');
    expect(f.nativeElement.textContent).toContain('323.00');   // 170 + 153
    ctrl.verify();
  });
});
