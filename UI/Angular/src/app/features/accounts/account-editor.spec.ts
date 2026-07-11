import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AccountEditor } from './account-editor';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AccountResponse } from '../../core/accounts/account';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function route(id: string | null, query: Record<string, string> = {}) {
  return { provide: ActivatedRoute, useValue: { snapshot: {
    paramMap: { get: () => id },
    queryParamMap: { get: (k: string) => query[k] ?? null },
  } } };
}
function seedAccounts(svc: AccountsService) {
  const a = (id: string, number: string, type: AccountResponse['type']): AccountResponse =>
    ({ id, number, name: 'n' + number, type, parentId: null, postable: true, requiredDimension: null, requiredDimensions: [], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
  (svc as unknown as { _accounts: { set(v: AccountResponse[]): void } })._accounts.set([a('cash', '1000', 'Asset'), a('rev', '4000', 'Revenue')]);
}

describe('AccountEditor', () => {
  let ctrl: HttpTestingController;
  function setup(id: string | null, seed = true) {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('gl.manageAccounts'), route(id)] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    if (seed) seedAccounts(TestBed.inject(AccountsService));
  }
  afterEach(() => ctrl.verify());

  it('create: required validation blocks save until number/name/type set, then PUTs a new id', () => {
    setup(null); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.canSave()).toBe(false);
    cmp.accountForm.number().value.set('4100'); cmp.accountForm.name().value.set('Service Revenue'); cmp.accountForm.type().value.set('Revenue');
    f.detectChanges();
    expect(cmp.canSave()).toBe(true);
    expect(cmp.normalSide()).toBe('Credit');             // Revenue is a credit-normal type
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const put = ctrl.expectOne(r => r.method === 'PUT' && /\/clients\/C1\/accounts\/.+/.test(r.url));
    expect(put.request.body.number).toBe('4100'); expect(put.request.body.type).toBe('Revenue');
    expect(put.request.body.normalSide).toBeUndefined(); // normalSide is derived, not persisted in PUT body
    put.flush({ id: 'x', number: '4100', name: 'Service Revenue', type: 'Revenue', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Credit', isTemporary: false });
    expect(nav).toHaveBeenCalledWith(['/accounts']);
  });

  it('edit: loads the account and PUTs the same id on save (renumber)', () => {
    setup('cash'); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.accountForm.number().value()).toBe('1000');
    cmp.accountForm.number().value.set('1001');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    f.detectChanges(); cmp.save();
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/cash');
    expect(put.request.body.number).toBe('1001');
    put.flush({ id: 'cash', number: '1001', name: 'n1000', type: 'Asset', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
    expect(nav).toHaveBeenCalledWith(['/accounts']);
  });

  it('edit: loads form reactively on cold cache (direct nav / hard refresh)', () => {
    setup('cash', false); // cold cache — accounts signal is empty
    const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    // Cache is cold; form must start blank before the HTTP response arrives
    expect(cmp.accountForm.number().value()).toBe('');
    // Flush the GET accounts that load() triggered on cold cache
    ctrl.expectOne('http://localhost:5000/clients/C1/accounts').flush([
      { id: 'cash', number: '1000', name: 'nCash', type: 'Asset', parentId: null, postable: true, requiredDimension: null, cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false },
    ]);
    f.detectChanges(); // let the effect observe the now-populated byId() and populate the form
    // Effect must have populated the form from the loaded account
    expect(cmp.accountForm.number().value()).toBe('1000');
  });

  it('surfaces a server 422 (duplicate number)', () => {
    setup(null); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.accountForm.number().value.set('4000'); cmp.accountForm.name().value.set('Dup'); cmp.accountForm.type().value.set('Asset');
    f.detectChanges(); cmp.save();
    ctrl.expectOne(r => r.method === 'PUT').flush({ detail: 'Account number 4000 already exists' }, { status: 422, statusText: 'Unprocessable' });
    f.detectChanges();
    expect(cmp.message()).toContain('already exists');
  });

  it('prefills id, type, name and dims from query params on a new account', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('gl.manageAccounts'),
      route(null, { id: 'expected-guid', type: 'Liability', name: 'Withholdings Payable', dims: 'Employee, PayPeriod' })] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    seedAccounts(TestBed.inject(AccountsService)); // avoid an unflushed cold-cache GET /accounts
    const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.accountForm.name().value()).toBe('Withholdings Payable');
    expect(cmp.accountForm.type().value()).toBe('Liability');
    expect(cmp.dimsText()).toBe('Employee, PayPeriod');

    cmp.accountForm.number().value.set('2100');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    f.detectChanges(); cmp.save();
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/expected-guid'); // prefilled id, not a random one
    expect(put.request.body.requiredDimensions).toEqual(['Employee', 'PayPeriod']);
    expect(put.request.body.type).toBe('Liability');
    put.flush({ id: 'expected-guid', number: '2100', name: 'Withholdings Payable', type: 'Liability', parentId: null, postable: true, requiredDimension: null, requiredDimensions: ['Employee', 'PayPeriod'], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Credit', isTemporary: false });
    expect(nav).toHaveBeenCalledWith(['/accounts']);
  });

  it('setDims parses a comma-separated string into a trimmed, non-empty array', () => {
    setup(null); const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    cmp.setDims('Customer,  Invoice ,,');
    expect(cmp.accountForm.requiredDimensions().value()).toEqual(['Customer', 'Invoice']);
    expect(cmp.dimsText()).toBe('Customer, Invoice');
  });

  it('edit preserves an existing account\'s dimensions through save', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('gl.manageAccounts'), route('ar')] });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const svc = TestBed.inject(AccountsService);
    (svc as unknown as { _accounts: { set(v: AccountResponse[]): void } })._accounts.set([
      { id: 'ar', number: '1100', name: 'A/R', type: 'Asset', parentId: null, postable: true, requiredDimension: null, requiredDimensions: ['Customer', 'Invoice'], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false },
    ]);
    const f = TestBed.createComponent(AccountEditor); f.detectChanges();
    const cmp = f.componentInstance;
    expect(cmp.dimsText()).toBe('Customer, Invoice');
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    cmp.save();
    const put = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/ar');
    expect(put.request.body.requiredDimensions).toEqual(['Customer', 'Invoice']);
    put.flush({ id: 'ar', number: '1100', name: 'A/R', type: 'Asset', parentId: null, postable: true, requiredDimension: null, requiredDimensions: ['Customer', 'Invoice'], cashFlowActivity: null, isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false });
    expect(nav).toHaveBeenCalledWith(['/accounts']);
  });
});
