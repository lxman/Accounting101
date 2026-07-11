import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChartHealthWidget } from './chart-health-widget';
import { ClientContextService } from '../../core/client/client-context.service';

const KEYS = ['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory'];

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  const ctrl = TestBed.inject(HttpTestingController);
  TestBed.inject(ClientContextService).select('C1');
  return { ctrl };
}

function flushAll(ctrl: HttpTestingController, overrides: Record<string, unknown> = {}) {
  for (const key of KEYS) {
    const body = overrides[key] ?? { moduleKey: key, ready: true, accounts: [] };
    ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush(body);
  }
}

describe('ChartHealthWidget', () => {
  it('shows the ready count out of six', () => {
    const { ctrl } = setup();
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl, { payroll: { moduleKey: 'payroll', ready: false, accounts: [
      { accountId: 'wh', label: 'Withholdings Payable', expectedType: 'Liability', requiredDimensions: [], status: 'Missing', actualType: null, actualRequiredDimensions: null, detail: 'add a Liability account' } ] } });
    f.detectChanges();
    expect(f.componentInstance.readyCount()).toBe(5);
    const text = (f.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('5 / 6');
    ctrl.verify();
  });

  it('builds a prefilled new-account link for a Missing gap', () => {
    const { ctrl } = setup();
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl, { payroll: { moduleKey: 'payroll', ready: false, accounts: [
      { accountId: 'wh-guid', label: 'Withholdings Payable', expectedType: 'Liability', requiredDimensions: ['Employee'], status: 'Missing', actualType: null, actualRequiredDimensions: null, detail: 'add a Liability account' } ] } });
    f.detectChanges();
    const gap = { accountId: 'wh-guid', label: 'Withholdings Payable', expectedType: 'Liability', requiredDimensions: ['Employee'], status: 'Missing' as const, actualType: null, actualRequiredDimensions: null, detail: '' };
    expect(f.componentInstance.gapLink(gap)).toEqual(['/accounts', 'new']);
    expect(f.componentInstance.gapQuery(gap)).toEqual({ id: 'wh-guid', type: 'Liability', name: 'Withholdings Payable', dims: 'Employee' });
    ctrl.verify();
  });

  it('builds an edit link for a non-Missing gap', () => {
    const { ctrl } = setup();
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl);
    f.detectChanges();
    const gap = { accountId: 'ar', label: 'A/R', expectedType: 'Asset', requiredDimensions: ['Customer'], status: 'MissingDimensions' as const, actualType: 'Asset', actualRequiredDimensions: [], detail: '' };
    expect(f.componentInstance.gapLink(gap)).toEqual(['/accounts', 'ar', 'edit']);
    expect(f.componentInstance.gapQuery(gap)).toBeUndefined();
    ctrl.verify();
  });
});
