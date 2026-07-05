import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DisposalDetail } from './disposal-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'd1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('DisposalDetail', () => {
  it('renders the gain/loss breakdown and Void for a posted disposal', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(DisposalDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/disposals/d1').flush({
      disposal: { id: 'd1', number: 'DP-1', assetId: 'a1', disposalDate: '2026-06-30', proceeds: 10000, catchUpDepreciation: 2500,
      accumulatedBeforeDisposal: 0, accumulatedAtDisposal: 2500, netBookValue: 9500, gainLoss: 500, memo: null, status: 'Posted' } });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries').flush([{ id: 'e1' }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('9,500'); // NBV
    expect(f.nativeElement.textContent).toContain('Void');
    ctrl.verify();
  });
});
