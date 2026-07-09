import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute } from '@angular/router';
import { ItemEditor } from './item-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(paramId: string | null) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('inventory.write'),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(paramId ? [['id', paramId]] : []) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('ItemEditor', () => {
  it('requires sku, name, and unitOfMeasure before allowing save', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(ItemEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as {
      sku: { set: (v: string) => void }; name: { set: (v: string) => void };
      unitOfMeasure: { set: (v: string) => void }; canSave: () => boolean;
    };
    expect(c.canSave()).toBe(false);
    c.sku.set('SKU-1'); c.name.set('Widget'); c.unitOfMeasure.set('ea');
    expect(c.canSave()).toBe(true);
    ctrl.verify();
  });

  it('create posts the mapped SaveItemRequest', () => {
    const ctrl = setup(null);
    const f = TestBed.createComponent(ItemEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as {
      sku: { set: (v: string) => void }; name: { set: (v: string) => void };
      unitOfMeasure: { set: (v: string) => void }; description: { set: (v: string | null) => void }; save: () => void;
    };
    c.sku.set('SKU-1'); c.name.set('Widget'); c.unitOfMeasure.set('ea'); c.description.set(null);
    c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/items');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ sku: 'SKU-1', name: 'Widget', description: null, unitOfMeasure: 'ea' });
    req.flush({ item: { id: 'i1', sku: 'SKU-1', name: 'Widget', description: null, unitOfMeasure: 'ea', status: 'Active', onHandQuantity: 0, totalValue: 0 }, averageUnitCost: 0 });
    ctrl.verify();
  });

  it('edit loads the existing item and PUTs on save', () => {
    const ctrl = setup('i1');
    const f = TestBed.createComponent(ItemEditor);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/items/i1').flush({
      item: { id: 'i1', sku: 'SKU-1', name: 'Widget', description: 'desc', unitOfMeasure: 'ea', status: 'Active', onHandQuantity: 5, totalValue: 250 },
      averageUnitCost: 50,
    });
    f.detectChanges();
    (f.componentInstance as unknown as { save: () => void }).save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/items/i1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ sku: 'SKU-1', name: 'Widget', description: 'desc', unitOfMeasure: 'ea' });
    req.flush({ item: { id: 'i1', sku: 'SKU-1', name: 'Widget', description: 'desc', unitOfMeasure: 'ea', status: 'Active', onHandQuantity: 5, totalValue: 250 }, averageUnitCost: 50 });
    ctrl.verify();
  });
});
