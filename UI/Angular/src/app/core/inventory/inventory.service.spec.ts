import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { InventoryService } from './inventory.service';
import { ClientContextService } from '../client/client-context.service';
import { Item, StockMovement } from './inventory';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  TestBed.inject(ClientContextService).select('C1');
  return { svc: TestBed.inject(InventoryService), ctrl: TestBed.inject(HttpTestingController) };
}

const item: Item = {
  id: 'i1', sku: 'SKU-1', name: 'Widget', description: null,
  unitOfMeasure: 'each', status: 'Active', onHandQuantity: 10, totalValue: 100,
};

const movement: StockMovement = {
  id: 'm1', number: 'MV-00001', itemId: 'i1', type: 'Receipt',
  effectiveDate: '2026-03-01', memo: null, quantity: 5,
  appliedUnitCost: 10, extendedCost: 50, status: 'Posted',
};

describe('InventoryService — items', () => {
  it('listItems sets query params and returns the paged ItemView envelope', () => {
    const { svc, ctrl } = setup();
    let page: { items: { item: Item }[]; total: number } = { items: [], total: 0 };
    svc.listItems({ skip: 0, limit: 50, order: 'asc', includeInactive: true }).subscribe(p => (page = p));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/items');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('skip')).toBe('0');
    expect(req.request.params.get('limit')).toBe('50');
    expect(req.request.params.get('order')).toBe('asc');
    expect(req.request.params.get('includeInactive')).toBe('true');
    req.flush({ items: [{ item, averageUnitCost: 10 }], total: 1, skip: 0, limit: 50 });
    expect(page.items[0].item.id).toBe('i1');
    expect(page.total).toBe(1);
    ctrl.verify();
  });

  it('getItem returns the ItemView envelope (not unwrapped)', () => {
    const { svc, ctrl } = setup();
    let got: { item?: Item; averageUnitCost?: number } = {};
    svc.getItem('i1').subscribe(v => (got = v));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/items/i1');
    expect(req.request.method).toBe('GET');
    req.flush({ item, averageUnitCost: 10 });
    expect(got.item?.id).toBe('i1');
    expect(got.averageUnitCost).toBe(10);
    ctrl.verify();
  });

  it('createItem posts SaveItemRequest and returns the ItemView envelope', () => {
    const { svc, ctrl } = setup();
    let got: { item?: Item } = {};
    svc.createItem({ sku: 'SKU-1', name: 'Widget', description: null, unitOfMeasure: 'each' }).subscribe(v => (got = v));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/items');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ sku: 'SKU-1', name: 'Widget', description: null, unitOfMeasure: 'each' });
    req.flush({ item, averageUnitCost: 0 }, { status: 201, statusText: 'Created' });
    expect(got.item?.id).toBe('i1');
    ctrl.verify();
  });

  it('updateItem puts SaveItemRequest and returns the ItemView envelope', () => {
    const { svc, ctrl } = setup();
    let got: { item?: Item } = {};
    svc.updateItem('i1', { sku: 'SKU-1', name: 'Widget v2', description: null, unitOfMeasure: 'each' }).subscribe(v => (got = v));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/items/i1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.name).toBe('Widget v2');
    req.flush({ item: { ...item, name: 'Widget v2' }, averageUnitCost: 10 });
    expect(got.item?.name).toBe('Widget v2');
    ctrl.verify();
  });

  it('deactivateItem posts and expects no body (204)', () => {
    const { svc, ctrl } = setup();
    let completed = false;
    svc.deactivateItem('i1').subscribe({ complete: () => (completed = true) });
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/items/i1/deactivate');
    expect(req.request.method).toBe('POST');
    req.flush(null, { status: 204, statusText: 'No Content' });
    expect(completed).toBe(true);
    ctrl.verify();
  });

  it('reactivateItem posts and returns the ItemView envelope', () => {
    const { svc, ctrl } = setup();
    let got: { item?: Item } = {};
    svc.reactivateItem('i1').subscribe(v => (got = v));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/items/i1/reactivate');
    expect(req.request.method).toBe('POST');
    req.flush({ item, averageUnitCost: 10 });
    expect(got.item?.status).toBe('Active');
    ctrl.verify();
  });
});

describe('InventoryService — movements', () => {
  it('recordMovement posts RecordMovementRequest with type serialized as a string and unwraps the movement', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string; type?: string } = {};
    svc.recordMovement({ itemId: 'i1', type: 'Receipt', quantity: 5, unitCost: 10, effectiveDate: '2026-03-01', memo: null })
      .subscribe(m => (got = m));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/movements');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.type).toBe('Receipt');
    req.flush({ movement }, { status: 201, statusText: 'Created' });
    expect(got.id).toBe('m1');
    expect(got.type).toBe('Receipt');
    ctrl.verify();
  });

  it('getMovement unwraps the wrapped view', () => {
    const { svc, ctrl } = setup();
    let got: { id?: string } = {};
    svc.getMovement('m1').subscribe(m => (got = m));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1');
    expect(req.request.method).toBe('GET');
    req.flush({ movement });
    expect(got.id).toBe('m1');
    ctrl.verify();
  });

  it('listMovements sets itemId + query params and unwraps each item to a raw movement', () => {
    const { svc, ctrl } = setup();
    let page: { items: StockMovement[]; total: number } = { items: [], total: 0 };
    svc.listMovements('i1', { skip: 0, limit: 20, order: 'desc', includeVoided: false }).subscribe(p => (page = p));
    const req = ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/movements');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('itemId')).toBe('i1');
    expect(req.request.params.get('skip')).toBe('0');
    expect(req.request.params.get('limit')).toBe('20');
    expect(req.request.params.get('order')).toBe('desc');
    expect(req.request.params.get('includeVoided')).toBe('false');
    req.flush({ items: [{ movement }], total: 1, skip: 0, limit: 20 });
    expect(page.items[0].id).toBe('m1');
    expect(page.total).toBe(1);
    ctrl.verify();
  });

  it('voidMovement posts a reason and unwraps the movement', () => {
    const { svc, ctrl } = setup();
    let got: { status?: string } = {};
    svc.voidMovement('m1', 'entered in error').subscribe(m => (got = m));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1/void');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'entered in error' });
    req.flush({ movement: { ...movement, status: 'Void' } });
    expect(got.status).toBe('Void');
    ctrl.verify();
  });
});
