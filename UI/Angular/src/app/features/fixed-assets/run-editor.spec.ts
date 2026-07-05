import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunEditor } from './run-editor';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('fixedassets.write')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('FA RunEditor', () => {
  it('posts the run request', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as { year: { set: (v: number) => void }; month: { set: (v: number) => void }; save: () => void };
    c.year.set(2026); c.month.set(1); c.save();
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs');
    expect(req.request.body).toEqual({ year: 2026, month: 1, effectiveDate: null, memo: null });
    req.flush({ run: { id: 'r1', number: 'DR-1', period: { year: 2026, month: 1 }, effectiveDate: '2026-01-31', memo: null, lines: [], total: 500, status: 'Posted' } });
    ctrl.verify();
  });

  it('shows the 422 nothing-to-depreciate message inline', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(RunEditor);
    f.detectChanges();
    const c = f.componentInstance as unknown as { year: { set: (v: number) => void }; month: { set: (v: number) => void }; save: () => void };
    c.year.set(2026); c.month.set(1); c.save();
    ctrl.expectOne('http://localhost:5000/clients/C1/depreciation-runs')
      .flush({ detail: 'No assets to depreciate for 2026-01.' }, { status: 422, statusText: 'Unprocessable' });
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No assets to depreciate');
    ctrl.verify();
  });
});
