import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { ChartOfAccounts } from './chart-of-accounts';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountResponse } from '../../core/accounts/account';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function seed(): AccountResponse[] {
  const a = (id: string, number: string, type: AccountResponse['type'], parentId: string | null = null, active = true): AccountResponse =>
    ({ id, number, name: 'n' + number, type, parentId, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active, normalSide: 'Debit', isTemporary: false });
  return [a('cash', '1000', 'Asset'), a('ar', '1200', 'Asset'), a('rev', '4000', 'Revenue'), a('old', '1900', 'Asset', null, false)];
}

describe('ChartOfAccounts', () => {
  let ctrl: HttpTestingController;
  function setup() {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('gl.manageAccounts')] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  }
  function flushData() {
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush(seed());
    ctrl.expectOne(r => r.url.includes('/clients/C1/trial-balance')).flush({ asOf: null, accounts: [{ accountId: 'cash', balance: 500 }, { accountId: 'rev', balance: -500 }] });
  }
  afterEach(() => ctrl.verify());

  it('renders type sections with balances and hides inactive by default', () => {
    setup(); const f = TestBed.createComponent(ChartOfAccounts); f.detectChanges(); flushData(); f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('Asset'); expect(text).toContain('1000 n1000');
    expect(text).toContain('500.00');                    // balance rendered via money()
    expect(text).not.toContain('1900');                 // inactive hidden
    f.componentInstance.showInactive.set(true); f.detectChanges();
    expect(f.nativeElement.textContent).toContain('1900'); // shown when toggled
  });

  it('a valid drop reparents via upsert; an invalid (cross-type) drop does not', () => {
    setup(); const f = TestBed.createComponent(ChartOfAccounts); f.detectChanges(); flushData(); f.detectChanges();
    const cmp = f.componentInstance;
    // valid: AR (asset) under Cash (asset)
    cmp.onDrop('ar', 'cash', 'Asset');
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/ar');
    expect(put.request.method).toBe('PUT'); expect(put.request.body.parentId).toBe('cash');
    put.flush({ ...seed().find(x => x.id === 'ar')!, parentId: 'cash' });
    // invalid: Cash (asset) under Revenue → no call
    cmp.onDrop('cash', 'rev', 'Asset');                 // canDrop false (cross-type at section level)
    ctrl.expectNone('http://localhost:5000/clients/C1/accounts/cash');
  });

  // Guards the CDK wiring the onDrop() unit tests above bypass. Both invariants were once broken,
  // so drops never fired live (drag animated, nothing persisted, refresh reverted):
  it('wires CDK so drops fire: each drag sits inside a drop list, and every list joins the group', () => {
    setup(); const f = TestBed.createComponent(ChartOfAccounts); f.detectChanges(); flushData(); f.detectChanges();
    const host: HTMLElement = f.nativeElement;
    const drags = [...host.querySelectorAll<HTMLElement>('.cdk-drag')];
    expect(drags.length).toBeGreaterThan(0);
    // A cdkDrag must live INSIDE a cdkDropList (not share the element) or CDK raises no drop event.
    for (const d of drags) {
      expect(d.classList.contains('cdk-drop-list')).toBe(false);
      expect(d.parentElement?.classList.contains('cdk-drop-list')).toBe(true);
    }
    // Every drop list (section headers + rows) must register with the group, else lists aren't
    // connected and a drag can't transfer into another list. The recursive #row template must be
    // declared inside cdkDropListGroup so its embedded views inherit the group from their injector.
    const group = f.debugElement.query(By.directive(CdkDropListGroup)).injector.get(CdkDropListGroup) as unknown as { _items: Set<unknown> };
    const lists = f.debugElement.queryAll(By.directive(CdkDropList));
    expect(lists.length).toBeGreaterThan(drags.length);   // headers + rows
    expect(group._items.size).toBe(lists.length);
  });
});
