import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute } from '@angular/router';
import { AssetEditor } from './asset-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(paramId: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(paramId ? [['id', paramId]] : []) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('AssetEditor', () => {
  it('shows the declining-balance factor field only for declining balance', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(AssetEditor);
    f.detectChanges();
    const cmp = f.componentInstance as unknown as { method: (m: string) => void; showFactor: () => boolean };
    expect(cmp.showFactor()).toBe(false);
    (f.componentInstance as unknown as { method: { set: (v: string) => void } }).method.set('DecliningBalance');
    expect((f.componentInstance as unknown as { showFactor: () => boolean }).showFactor()).toBe(true);
    ctrl.verify();
  });

  it('create posts the mapped SaveAssetRequest', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(AssetEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as {
      description: { set: (v: string) => void }; acquisitionCost: { set: (v: number) => void };
      inServiceDate: { set: (v: string) => void }; usefulLifeMonths: { set: (v: number) => void };
      salvageValue: { set: (v: number) => void }; save: () => void;
    };
    c.description.set('Van'); c.acquisitionCost.set(12000); c.inServiceDate.set('2026-01-01');
    c.usefulLifeMonths.set(24); c.salvageValue.set(0);
    c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/assets');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ description: 'Van', acquisitionCost: 12000, inServiceDate: '2026-01-01',
      usefulLifeMonths: 24, salvageValue: 0, method: 'StraightLine', decliningBalanceFactor: null });
    req.flush({ asset: { id: 'a1' }, netBookValue: 12000 });
    ctrl.verify();
  });
});
