import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FixedAssetsService } from './fixed-assets.service';
import { ClientContextService } from '../client/client-context.service';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(FixedAssetsService), ctrl: TestBed.inject(HttpTestingController) };
}

describe('FixedAssetsService', () => {
  it('listAssets keeps the AssetView (with net book value)', () => {
    const { svc, ctrl } = setup();
    let items: unknown[] = [];
    svc.listAssets({ skip: 0, limit: 50 }).subscribe(p => (items = p.items));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/assets');
    expect(req.request.params.get('skip')).toBe('0');
    req.flush({ items: [{ asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01',
      usefulLifeMonths: 24, salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null,
      status: 'Active', accumulatedDepreciation: 500 }, netBookValue: 11500 }], total: 1, skip: 0, limit: 50 });
    expect((items[0] as { netBookValue: number }).netBookValue).toBe(11500);
    ctrl.verify();
  });

  it('listRuns maps the run-view envelope to bare runs', () => {
    const { svc, ctrl } = setup();
    let items: unknown[] = [];
    svc.listRuns({ skip: 0, limit: 50, includeVoided: true }).subscribe(p => (items = p.items));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/depreciation-runs');
    expect(req.request.params.get('includeVoided')).toBe('true');
    req.flush({ items: [{ run: { id: 'r1', number: 'DR-00001', period: { year: 2026, month: 1 },
      effectiveDate: '2026-01-31', memo: null, lines: [{ assetId: 'a1', amount: 500 }], total: 500, status: 'Posted' } }],
      total: 1, skip: 0, limit: 50 });
    expect((items[0] as { total: number }).total).toBe(500);
    ctrl.verify();
  });

  it('runDepreciation posts and unwraps the run view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.runDepreciation({ year: 2026, month: 1, effectiveDate: null, memo: null }).subscribe(r => (got = r));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs');
    expect(req.request.method).toBe('POST');
    req.flush({ run: { id: 'r1', number: 'DR-00001', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31',
      memo: null, lines: [], total: 500, status: 'Posted' } });
    expect(got.id).toBe('r1');
    ctrl.verify();
  });

  it('disposeAsset posts to the asset and unwraps the disposal view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.disposeAsset('a1', { disposalDate: '2026-06-30', proceeds: 10000, memo: null }).subscribe(d => (got = d));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1/dispose');
    expect(req.request.method).toBe('POST');
    req.flush({ disposal: { id: 'd1', number: 'DP-00001', assetId: 'a1', disposalDate: '2026-06-30', proceeds: 10000,
      catchUpDepreciation: 2500, accumulatedBeforeDisposal: 0, accumulatedAtDisposal: 2500, netBookValue: 9500,
      gainLoss: 500, memo: null, status: 'Posted' } });
    expect(got.id).toBe('d1');
    ctrl.verify();
  });

  it('entriesForSource sets the sourceRef param', () => {
    const { svc, ctrl } = setup();
    svc.entriesForSource('d1').subscribe();
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries');
    expect(req.request.params.get('sourceRef')).toBe('d1');
    req.flush([]);
    ctrl.verify();
  });
});
