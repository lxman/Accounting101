import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DisposeEditor } from './dispose-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'a1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('DisposeEditor', () => {
  it('loads the asset summary and posts the disposal', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(DisposeEditor);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1').flush({
      asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01', usefulLifeMonths: 24,
        salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null, status: 'Active', accumulatedDepreciation: 2500 },
      netBookValue: 9500 });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Van');
    const c = f.componentInstance as unknown as { disposalDate: { set: (v: string) => void }; proceeds: { set: (v: number) => void }; save: () => void };
    c.disposalDate.set('2026-06-30'); c.proceeds.set(10000); c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1/dispose');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ disposalDate: '2026-06-30', proceeds: 10000, memo: null });
    req.flush({ disposal: { id: 'd1', number: 'DP-1', assetId: 'a1', disposalDate: '2026-06-30', proceeds: 10000,
      catchUpDepreciation: 0, accumulatedBeforeDisposal: 2500, accumulatedAtDisposal: 2500, netBookValue: 9500, gainLoss: 500, memo: null, status: 'Posted' } });
    ctrl.verify();
  });

  it('shows a server error inline on a rejected dispose', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(DisposeEditor);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1').flush({
      asset: { id: 'a1', description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01', usefulLifeMonths: 24,
        salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null, status: 'Active', accumulatedDepreciation: 0 }, netBookValue: 12000 });
    f.detectChanges();
    const c = f.componentInstance as unknown as { disposalDate: { set: (v: string) => void }; proceeds: { set: (v: number) => void }; save: () => void };
    c.disposalDate.set('2026-06-30'); c.proceeds.set(1000); c.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/assets/a1/dispose')
      .flush({ detail: 'Asset a1 is Disposed; only an active asset can be disposed.' }, { status: 409, statusText: 'Conflict' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('only an active asset can be disposed');
    ctrl.verify();
  });
});
