import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ChartHealthWidget } from './chart-health-widget';
import { ClientContextService } from '../../core/client/client-context.service';
import { CapabilityService } from '../../core/capabilities/capability.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

const KEYS = ['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory'];

// Read caps for all six modules — needed so the visibility filter (Task 4) still shows all six
// in tests that aren't specifically exercising the filter itself.
const ALL_READ_CAPS = ['ar.read', 'ap.read', 'payroll.read', 'cash.read', 'fixedassets.read', 'inventory.read'];

const GAP = { accountId: 'wh', label: 'Withholdings Payable', expectedType: 'Liability', requiredDimensions: [],
  status: 'Missing', actualType: null, actualRequiredDimensions: null, detail: 'add a Liability account' };
const notReadyPayroll = { payroll: { moduleKey: 'payroll', ready: false, accounts: [GAP] } };
const fixLinks = (el: HTMLElement) => [...el.querySelectorAll('a')].filter(a => /Fix/.test(a.textContent ?? ''));

// Defaults to a user who CAN manage accounts and can read all six modules, so the Fix link
// renders and all six modules are visible; pass ALL_READ_CAPS (no gl.manageAccounts) for a
// read-only user who can still see every module.
function setup(caps: string[] = [...ALL_READ_CAPS, 'gl.manageAccounts']) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
      provideCapabilities(...caps)],
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

  it('shows only the modules the user has read capability for', () => {
    // Caps: cash.read + payroll.read only.
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]),
      provideHttpClient(), provideHttpClientTesting(), provideCapabilities('cash.read', 'payroll.read')] });
    const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    for (const key of ['cash', 'payroll']) // only these two are requested
      ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush({ moduleKey: key, ready: true, accounts: [] });
    f.detectChanges();
    expect(f.componentInstance.total()).toBe(2);
    expect((f.nativeElement as HTMLElement).textContent).toContain('2 / 2');
    ctrl.verify(); // proves NO request was made for the other four modules
  });

  it('an admin sees all six modules', () => {
    // Deployment admin: no per-module caps needed.
    const stub = provideCapabilities(); // empty caps
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]),
      provideHttpClient(), provideHttpClientTesting(), stub] });
    (TestBed.inject(CapabilityService) as unknown as { setDeploymentAdmin(v: boolean): void }).setDeploymentAdmin(true);
    const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    for (const key of ['receivables', 'payables', 'payroll', 'cash', 'fixedassets', 'inventory'])
      ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`).flush({ moduleKey: key, ready: true, accounts: [] });
    f.detectChanges();
    expect(f.componentInstance.total()).toBe(6);
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

  it('renders "couldn\'t check" for a module whose host errored', () => {
    const { ctrl } = setup();
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    for (const key of KEYS) {
      const req = ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`);
      if (key === 'payroll') req.flush('boom', { status: 400, statusText: 'Bad Request' });
      else req.flush({ moduleKey: key, ready: true, accounts: [] });
    }
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain("couldn't check");
    expect(f.componentInstance.readyCount()).toBe(5);
    ctrl.verify();
  });

  it('shows the Fix link on an expanded gap when the user can manage accounts', () => {
    const { ctrl } = setup([...ALL_READ_CAPS, 'gl.manageAccounts']);
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl, notReadyPayroll); f.detectChanges();
    f.componentInstance.toggle('payroll'); f.detectChanges();
    expect(fixLinks(f.nativeElement as HTMLElement).length).toBe(1);
    ctrl.verify();
  });

  it('hides the Fix link for a user without gl.manageAccounts, but still shows the gap detail', () => {
    const { ctrl } = setup(ALL_READ_CAPS);
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();
    flushAll(ctrl, notReadyPayroll); f.detectChanges();
    f.componentInstance.toggle('payroll'); f.detectChanges();
    const el = f.nativeElement as HTMLElement;
    expect(fixLinks(el).length).toBe(0);                          // Fix hidden — read-only user
    expect(el.textContent).toContain('Withholdings Payable');     // but the gap is still surfaced
    ctrl.verify();
  });

  it('cancels an in-flight load when the client changes, so stale data cannot win', () => {
    const { ctrl } = setup();
    const client = TestBed.inject(ClientContextService);
    const f = TestBed.createComponent(ChartHealthWidget); f.detectChanges();

    // Six pending C1 requests are outstanding; capture them without answering.
    const c1Reqs = KEYS.map(key => ctrl.expectOne(`http://localhost:5000/clients/C1/${key}/chart-readiness`));

    // Switch client before C1 resolves — the effect re-runs and onCleanup unsubscribes C1's forkJoin.
    client.select('C2'); f.detectChanges();

    // C1's HTTP requests were cancelled by the unsubscribe.
    expect(c1Reqs.every(r => r.cancelled)).toBe(true);

    // Answer C2 with a distinguishable payload (payables NOT ready → readyCount 5).
    for (const key of KEYS) {
      const body = key === 'payables'
        ? { moduleKey: 'payables', ready: false, accounts: [
            { accountId: 'ap', label: 'A/P', expectedType: 'Liability', requiredDimensions: [], status: 'Missing', actualType: null, actualRequiredDimensions: null, detail: 'add a Liability account' } ] }
        : { moduleKey: key, ready: true, accounts: [] };
      ctrl.expectOne(`http://localhost:5000/clients/C2/${key}/chart-readiness`).flush(body);
    }
    f.detectChanges();

    // Final state reflects C2, not C1.
    expect(f.componentInstance.readyCount()).toBe(5);
    expect(f.componentInstance.modules().find(m => m.key === 'payables')?.report?.ready).toBe(false);
    ctrl.verify();
  });
});
