import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MovementEditor } from './movement-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('inventory.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: new Map([['itemId', 'i1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

type Harness = {
  type: { set: (v: string) => void }; direction: { set: (v: string) => void };
  quantityMagnitude: { set: (v: number) => void }; unitCost: { set: (v: number) => void };
  effectiveDate: { set: (v: string) => void }; showUnitCost: () => boolean; save: () => void;
};

describe('MovementEditor', () => {
  it('shows unit cost for Receipt, hides for Issue', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MovementEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as Harness;
    expect(c.showUnitCost()).toBe(true);
    c.type.set('Issue');
    expect(c.showUnitCost()).toBe(false);
    ctrl.verify();
  });

  it('shows unit cost for Adjustment/Overage, hides for Adjustment/Shrinkage', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MovementEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as Harness;
    c.type.set('Adjustment');
    expect(c.showUnitCost()).toBe(true); // default direction Overage
    c.direction.set('Shrinkage');
    expect(c.showUnitCost()).toBe(false);
    ctrl.verify();
  });

  it('posts a Receipt with a positive quantity and the unit cost', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MovementEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as Harness;
    c.quantityMagnitude.set(10); c.unitCost.set(5); c.effectiveDate.set('2026-07-01');
    c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/movements');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ itemId: 'i1', type: 'Receipt', quantity: 10, unitCost: 5, effectiveDate: '2026-07-01', memo: null });
    req.flush({ movement: { id: 'm1', number: 'MV-1', itemId: 'i1', type: 'Receipt', effectiveDate: '2026-07-01', memo: null,
      quantity: 10, appliedUnitCost: 5, extendedCost: 50, resultingOnHand: 10, resultingTotalValue: 50, status: 'Posted' } });
    ctrl.verify();
  });

  it('posts an Issue with a positive quantity and no unit cost', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MovementEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as Harness;
    c.type.set('Issue'); c.quantityMagnitude.set(4); c.effectiveDate.set('2026-07-01');
    c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/movements');
    expect(req.request.body).toEqual({ itemId: 'i1', type: 'Issue', quantity: 4, unitCost: null, effectiveDate: '2026-07-01', memo: null });
    req.flush({ movement: { id: 'm2', number: 'MV-2', itemId: 'i1', type: 'Issue', effectiveDate: '2026-07-01', memo: null,
      quantity: 4, appliedUnitCost: 5, extendedCost: 20, resultingOnHand: 6, resultingTotalValue: 30, status: 'Posted' } });
    ctrl.verify();
  });

  it('posts a shrinkage Adjustment with a negative quantity and no unit cost', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MovementEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as Harness;
    c.type.set('Adjustment'); c.direction.set('Shrinkage'); c.quantityMagnitude.set(2); c.effectiveDate.set('2026-07-01');
    c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/movements');
    expect(req.request.body).toEqual({ itemId: 'i1', type: 'Adjustment', quantity: -2, unitCost: null, effectiveDate: '2026-07-01', memo: null });
    req.flush({ movement: { id: 'm3', number: 'MV-3', itemId: 'i1', type: 'Adjustment', effectiveDate: '2026-07-01', memo: null,
      quantity: -2, appliedUnitCost: 5, extendedCost: 10, resultingOnHand: 4, resultingTotalValue: 20, status: 'Posted' } });
    ctrl.verify();
  });

  it('posts an overage Adjustment with a positive quantity and the unit cost', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MovementEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as Harness;
    c.type.set('Adjustment'); c.quantityMagnitude.set(3); c.unitCost.set(6); c.effectiveDate.set('2026-07-01');
    c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/movements');
    expect(req.request.body).toEqual({ itemId: 'i1', type: 'Adjustment', quantity: 3, unitCost: 6, effectiveDate: '2026-07-01', memo: null });
    req.flush({ movement: { id: 'm4', number: 'MV-4', itemId: 'i1', type: 'Adjustment', effectiveDate: '2026-07-01', memo: null,
      quantity: 3, appliedUnitCost: 6, extendedCost: 18, resultingOnHand: 7, resultingTotalValue: 38, status: 'Posted' } });
    ctrl.verify();
  });
});
