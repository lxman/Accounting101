import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ItemDetail } from './item-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'i1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const itemBody = (onHandQuantity: number) => ({
  item: { id: 'i1', sku: 'SKU-1', name: 'Widget', description: null, unitOfMeasure: 'ea', status: 'Active', onHandQuantity, totalValue: onHandQuantity * 50 },
  averageUnitCost: 50,
});

describe('ItemDetail', () => {
  it('renders item summary and movement history', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(ItemDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/items/i1').flush(itemBody(10));
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/movements').flush({
      items: [{ movement: { id: 'm1', number: 'MV-1', itemId: 'i1', type: 'Receipt', effectiveDate: '2026-07-01', memo: null,
        quantity: 10, appliedUnitCost: 50, extendedCost: 500, resultingOnHand: 10, resultingTotalValue: 500, status: 'Posted' } }],
      total: 1, skip: 0, limit: 20,
    });
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('SKU-1');
    expect(text).toContain('Widget');
    expect(text).toContain('MV-1');
    expect(text).toContain('Receipt');
    ctrl.verify();
  });

  it('disables Deactivate when on-hand quantity is nonzero', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(ItemDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/items/i1').flush(itemBody(5));
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/movements').flush({ items: [], total: 0, skip: 0, limit: 20 });
    f.detectChanges();
    const btn = Array.from(f.nativeElement.querySelectorAll('button')).find((b: any) => b.textContent.includes('Deactivate')) as HTMLButtonElement;
    expect(btn).toBeTruthy();
    expect(btn.disabled).toBe(true);
    ctrl.verify();
  });

  it('enables Deactivate when on-hand quantity is zero', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(ItemDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/items/i1').flush(itemBody(0));
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/movements').flush({ items: [], total: 0, skip: 0, limit: 20 });
    f.detectChanges();
    const btn = Array.from(f.nativeElement.querySelectorAll('button')).find((b: any) => b.textContent.includes('Deactivate')) as HTMLButtonElement;
    expect(btn).toBeTruthy();
    expect(btn.disabled).toBe(false);
    ctrl.verify();
  });
});
