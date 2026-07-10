import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MovementDetail } from './movement-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'm1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

const movementBody = (status: 'Posted' | 'Void' = 'Posted') => ({
  movement: { id: 'm1', number: 'MV-1', itemId: 'i1', type: 'Receipt', effectiveDate: '2026-07-01', memo: null,
    quantity: 10, appliedUnitCost: 5, extendedCost: 50, status },
});

describe('MovementDetail', () => {
  it('renders the full movement snapshot with a Void button for a posted movement', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(MovementDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1').flush(movementBody());
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('MV-1');
    expect(text).toContain('Receipt');
    expect(text).toContain('Void');
    ctrl.verify();
  });

  it('void calls voidMovement and reloads', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(MovementDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1').flush(movementBody());
    f.detectChanges();
    (f.componentInstance as unknown as { voidMovement: () => void }).voidMovement();
    const voidReq = ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1/void');
    expect(voidReq.request.method).toBe('POST');
    voidReq.flush(movementBody('Void'));
    ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1').flush(movementBody('Void'));
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Void');
    ctrl.verify();
  });

  it('renders a 409 conflict as an inline error rather than hiding the Void button', () => {
    const ctrl = setup(['inventory.write']);
    const f = TestBed.createComponent(MovementDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1').flush(movementBody());
    f.detectChanges();
    (f.componentInstance as unknown as { voidMovement: () => void }).voidMovement();
    const voidReq = ctrl.expectOne('http://localhost:5000/clients/C1/movements/m1/void');
    voidReq.flush({ detail: 'A later movement depends on this one.' }, { status: 409, statusText: 'Conflict' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('A later movement depends on this one.');
    const btn = Array.from(f.nativeElement.querySelectorAll('button')).find((b: any) => b.textContent.includes('Void')) as HTMLButtonElement;
    expect(btn).toBeTruthy();
    ctrl.verify();
  });
});
