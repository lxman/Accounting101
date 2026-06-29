import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EntryForm } from './entry-form';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountResponse } from '../../core/accounts/account';

function seedAccounts(): AccountResponse[] {
  return [
    { id: 'A', number: '1000', name: 'Cash', type: 'Asset', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false },
    { id: 'B', number: '4000', name: 'Revenue', type: 'Revenue', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Credit', isTemporary: false },
  ];
}

describe('EntryForm', () => {
  let ctrl: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const accounts = TestBed.inject(AccountsService);
    (accounts as unknown as { _accounts: { set(v: AccountResponse[]): void } })._accounts.set(seedAccounts());
  });
  afterEach(() => ctrl.verify());

  it('disables Post until the entry is balanced with ≥2 lines', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    // two empty lines, no amounts → invalid
    expect(cmp.canPost()).toBe(false);
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(100);
    f.detectChanges();
    expect(cmp.canPost()).toBe(true);
  });

  it('flags unbalanced entries', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(90);
    f.detectChanges();
    expect(cmp.canPost()).toBe(false);
    expect(cmp.balanceError()).toContain('equal');
  });

  it('maps the two-column model to PostLineRequest and POSTs on submit', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.entryForm.effectiveDate().value.set('2026-06-29');
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(100);
    f.detectChanges();
    cmp.post();
    const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries');
    expect(http.request.body.lines).toEqual([
      { accountId: 'A', direction: 'Debit', amount: 100 },
      { accountId: 'B', direction: 'Credit', amount: 100 },
    ]);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate');
    http.flush({ id: 'E1', status: 'Active', posting: 'PendingApproval' });
    expect(nav).toHaveBeenCalledWith(['/journal', 'E1']);
  });

  it('surfaces a server 422 from the Validate button', () => {
    const f = TestBed.createComponent(EntryForm); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.setAccount(0, 'A'); cmp.entryForm.lines[0].debit().value.set(100);
    cmp.setAccount(1, 'B'); cmp.entryForm.lines[1].credit().value.set(100);
    f.detectChanges();
    cmp.validate();
    const http = ctrl.expectOne('http://localhost:5000/clients/C1/entries/validate');
    http.flush({ detail: 'Account 1000 is not postable' }, { status: 422, statusText: 'Unprocessable' });
    f.detectChanges();
    expect(cmp.serverMessage()).toContain('not postable');
  });
});
