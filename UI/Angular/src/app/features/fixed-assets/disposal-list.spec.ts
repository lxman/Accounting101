import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DisposalList } from './disposal-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('DisposalList', () => {
  it('renders disposals with signed gain/loss', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(DisposalList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/disposals').flush({
      items: [{ disposal: { id: 'd1', number: 'DP-00001', assetId: 'a1abcdef', disposalDate: '2026-06-30', proceeds: 10000,
        catchUpDepreciation: 2500, accumulatedBeforeDisposal: 0, accumulatedAtDisposal: 2500, netBookValue: 9500, gainLoss: 500, memo: null, status: 'Posted' } }],
      total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('DP-00001');
    expect(f.nativeElement.textContent).toContain('500');
    ctrl.verify();
  });
});
