import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router } from '@angular/router';
import { ItemList } from './item-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('ItemList', () => {
  it('renders items with sku, on-hand and avg cost', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(ItemList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/items').flush({
      items: [{ item: { id: 'i1', sku: 'SKU-1', name: 'Widget', description: null, unitOfMeasure: 'ea',
        status: 'Active', onHandQuantity: 10, totalValue: 500 }, averageUnitCost: 50 }],
      total: 1, skip: 0, limit: 50,
    });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('SKU-1');
    expect(f.nativeElement.textContent).toContain('Widget');
    expect(f.nativeElement.textContent).toContain('10');
    ctrl.verify();
  });

  it('whole-row click navigates to item detail', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(ItemList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/items').flush({
      items: [{ item: { id: 'i1', sku: 'SKU-1', name: 'Widget', description: null, unitOfMeasure: 'ea',
        status: 'Active', onHandQuantity: 10, totalValue: 500 }, averageUnitCost: 50 }],
      total: 1, skip: 0, limit: 50,
    });
    f.detectChanges();
    const navSpy = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const row = f.nativeElement.querySelector('tbody tr') as HTMLElement;
    row.click();
    expect(navSpy).toHaveBeenCalledWith(['/inventory/items', 'i1']);
    ctrl.verify();
  });

  it('hides "New item" without inventory.write', () => {
    const ctrl = setup([]);
    const f = TestBed.createComponent(ItemList);
    f.detectChanges();
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/items').flush({ items: [], total: 0, skip: 0, limit: 50 });
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).not.toContain('New item');
    ctrl.verify();
  });
});
