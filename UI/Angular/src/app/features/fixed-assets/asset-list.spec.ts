import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AssetList } from './asset-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('AssetList', () => {
  it('renders assets with net book value', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(AssetList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/assets').flush({
      items: [{ asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01',
        usefulLifeMonths: 24, salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null,
        status: 'Active', accumulatedDepreciation: 500 }, netBookValue: 11500 }], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Van');
    expect(f.nativeElement.textContent).toContain('11,500');
    ctrl.verify();
  });

  it('hides "New asset" without fixedassets.write', () => {
    const ctrl = setup([]);
    const f = TestBed.createComponent(AssetList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/assets').flush({ items: [], total: 0, skip: 0, limit: 50 });
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).not.toContain('New asset');
    ctrl.verify();
  });
});
