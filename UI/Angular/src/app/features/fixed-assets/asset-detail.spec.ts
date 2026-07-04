import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AssetDetail } from './asset-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'a1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

function flushAsset(ctrl: HttpTestingController, status: string) {
  ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1').flush({
    asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01', usefulLifeMonths: 24,
      salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null, status, accumulatedDepreciation: 2500 },
    netBookValue: 9500 });
}

describe('AssetDetail', () => {
  it('shows Dispose for an active asset with write cap', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(AssetDetail);
    f.detectChanges(); flushAsset(ctrl, 'Active'); f.detectChanges();
    expect(f.nativeElement.textContent).toContain('9,500'); // net book value
    expect(f.nativeElement.textContent).toContain('Dispose');
    ctrl.verify();
  });

  it('hides Dispose for a disposed asset and shows the disposals link', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(AssetDetail);
    f.detectChanges(); flushAsset(ctrl, 'Disposed'); f.detectChanges();
    expect(f.nativeElement.textContent).not.toContain('Dispose asset');
    expect(f.nativeElement.textContent).toContain('View disposals');
    ctrl.verify();
  });
});
