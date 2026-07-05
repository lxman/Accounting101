import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunDetail } from './run-detail';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup(caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities(...caps),
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['id', 'r1']]) } } }],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('FA RunDetail', () => {
  it('renders lines + total and resolves the posted entry link', () => {
    const ctrl = setup(['fixedassets.write']);
    const f = TestBed.createComponent(RunDetail);
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs/r1').flush({
      run: { id: 'r1', number: 'DR-1', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31', memo: null,
        lines: [{ assetId: 'a1', amount: 500 }], total: 500, status: 'Posted' } });
    ctrl.expectOne(r => r.url === 'http://localhost:5000/clients/C1/entries').flush([{ id: 'e1' }]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('500');
    expect(f.nativeElement.textContent).toContain('Void');
    ctrl.verify();
  });
});
