import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { PostingAccountsScreen } from './posting-accounts';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function seed(...caps: string[]) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities(...caps)],
  });
  TestBed.inject(ClientContextService).select('c1');
}

const base = `${environment.apiBaseUrl}/clients/c1`;
const cashSlot = { moduleKey: 'cash', slotKey: 'Cash', label: 'Cash / bank account', expectedType: 'Asset', requiredDimensions: [], currentAccountId: null };
const accounts = [
  { id: 'a1', number: '1000', name: 'Business Checking', type: 'Asset', postable: true },
  { id: 'a2', number: '4000', name: 'Sales', type: 'Revenue', postable: true },
];

describe('PostingAccountsScreen', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  function boot(caps: string[]) {
    seed(...caps); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();
    http.expectOne(`${base}/posting-accounts`).flush({ slots: [cashSlot] });
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();
    return f;
  }

  it('renders the Cash slot and PUTs the chosen account', () => {
    const f = boot(['admin.postingAccounts']);
    const c = f.componentInstance as PostingAccountsScreen;
    expect(c.slots().length).toBe(1);

    c.selectAccount('cash', 'Cash', 'a1');
    c.save('cash');
    const req = http.expectOne(`${base}/posting-accounts/cash`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ slots: { Cash: 'a1' } });
    req.flush({ moduleKey: 'cash', slots: { Cash: 'a1' } });
    expect(c.savedModule()).toBe('cash');
  });

  it('hides Save without admin.postingAccounts', () => {
    const f = boot(['gl.read']);
    expect((f.nativeElement as HTMLElement).querySelector('button')).toBeNull();
  });

  it('preselects the current account when the chart loads in a later CD cycle', () => {
    seed('admin.postingAccounts'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();

    // slots resolve first, in their own change-detection cycle...
    http.expectOne(`${base}/posting-accounts`)
      .flush({ slots: [{ ...cashSlot, currentAccountId: 'a1' }] });
    f.detectChanges();

    // ...then the chart resolves in a separate cycle (the live-stack timing).
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();

    const select = (f.nativeElement as HTMLElement)
      .querySelector('[data-testid="slot-cash-Cash"]') as HTMLSelectElement;
    expect(select).not.toBeNull();
    expect(select.value).toBe('a1');
    const selectedText = select.options[select.selectedIndex].textContent ?? '';
    expect(selectedText).toContain('Business Checking');
  });

  it('omits an unset slot from the PUT for a multi-slot module', () => {
    seed('admin.postingAccounts'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();
    const twoSlots = [
      { moduleKey: 'payroll', slotKey: 'Cash', label: 'Cash', expectedType: 'Asset', requiredDimensions: [], currentAccountId: null },
      { moduleKey: 'payroll', slotKey: 'SalariesExpense', label: 'Salaries Expense', expectedType: 'Expense', requiredDimensions: [], currentAccountId: null },
    ];
    http.expectOne(`${base}/posting-accounts`).flush({ slots: twoSlots });
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();

    const c = f.componentInstance as PostingAccountsScreen;
    c.selectAccount('payroll', 'Cash', 'a1');   // set Cash; leave SalariesExpense at default
    c.save('payroll');
    const req = http.expectOne(`${base}/posting-accounts/payroll`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ slots: { Cash: 'a1' } });   // SalariesExpense omitted
    req.flush({ moduleKey: 'payroll', slots: { Cash: 'a1' } });
  });

  it('shows only the error when the slots GET fails, hiding the loading indicator', () => {
    seed('admin.postingAccounts'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();

    http.expectOne(`${base}/posting-accounts`).flush('fail', { status: 500, statusText: 'Server Error' });
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();

    const text = (f.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Could not load posting accounts.');
    expect(text).not.toContain('Loading…');
  });

  const rxSlot = { moduleKey: 'receivables', slotKey: 'Revenue', label: 'Revenue', expectedType: 'Revenue', requiredDimensions: [], currentAccountId: null };

  function bootReceivables(categories: Record<string, string>, source: 'stored' | 'config') {
    seed('admin.postingAccounts'); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(PostingAccountsScreen);
    f.detectChanges();
    http.expectOne(`${base}/posting-accounts`).flush({ slots: [rxSlot] });
    http.expectOne(`${base}/accounts`).flush(accounts);
    f.detectChanges();
    http.expectOne(`${base}/posting-accounts/receivables/revenue-categories`)
      .flush({ moduleKey: 'receivables', categories, source });
    f.detectChanges();
    return f;
  }

  it('does not request revenue categories when receivables is not among the slots', () => {
    boot(['admin.postingAccounts']);   // cash-only boot from the existing helper
    http.expectNone(`${base}/posting-accounts/receivables/revenue-categories`);
  });

  it('renders category rows from the GET and notes the config source', () => {
    const f = bootReceivables({ Consulting: 'a2' }, 'config');
    const el = f.nativeElement as HTMLElement;
    const name = el.querySelector('[data-testid="category-name-0"]') as HTMLInputElement;
    expect(name.value).toBe('Consulting');
    const select = el.querySelector('[data-testid="category-account-0"]') as HTMLSelectElement;
    expect(select.value).toBe('a2');
    expect(el.textContent).toContain('deployment defaults');
  });

  it('saves the rows as a full-replace PUT and flips the source note off', () => {
    const f = bootReceivables({ Consulting: 'a2' }, 'config');
    const c = f.componentInstance as PostingAccountsScreen;
    c.addCategory();
    c.setCategoryName(1, 'License');
    c.setCategoryAccount(1, 'a1');
    c.saveCategories();
    const req = http.expectOne(`${base}/posting-accounts/receivables/revenue-categories`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ categories: { Consulting: 'a2', License: 'a1' } });
    req.flush({ moduleKey: 'receivables', categories: { Consulting: 'a2', License: 'a1' }, source: 'stored' });
    f.detectChanges();
    expect(c.categoriesSaved()).toBe(true);
    expect((f.nativeElement as HTMLElement).textContent).not.toContain('deployment defaults');
  });

  it('deleting a row removes it from the next save', () => {
    const f = bootReceivables({ Consulting: 'a2', License: 'a1' }, 'stored');
    const c = f.componentInstance as PostingAccountsScreen;
    c.removeCategory(0);
    c.saveCategories();
    const req = http.expectOne(`${base}/posting-accounts/receivables/revenue-categories`);
    expect(req.request.body).toEqual({ categories: { License: 'a1' } });
    req.flush({ moduleKey: 'receivables', categories: { License: 'a1' }, source: 'stored' });
  });

  it('blocks save on duplicate names (case-sensitive: differing case is allowed)', () => {
    const f = bootReceivables({ Consulting: 'a2' }, 'stored');
    const c = f.componentInstance as PostingAccountsScreen;
    c.addCategory();
    c.setCategoryName(1, 'Consulting');
    c.setCategoryAccount(1, 'a1');
    expect(c.categoryValidation()).toContain('unique');
    c.saveCategories();
    http.expectNone(`${base}/posting-accounts/receivables/revenue-categories`);

    c.setCategoryName(1, 'consulting');   // different case — distinct, valid
    expect(c.categoryValidation()).toBeNull();
  });

  it('blocks save on blank, dotted, or dollar-prefixed names and unset accounts', () => {
    const f = bootReceivables({}, 'stored');
    const c = f.componentInstance as PostingAccountsScreen;
    c.addCategory();
    expect(c.categoryValidation()).not.toBeNull();          // blank name
    c.setCategoryName(0, 'Prof.Services');
    c.setCategoryAccount(0, 'a1');
    expect(c.categoryValidation()).toContain('.');
    c.setCategoryName(0, '$bad');
    expect(c.categoryValidation()).not.toBeNull();
    c.setCategoryName(0, 'Services');
    c.setCategoryAccount(0, '');
    expect(c.categoryValidation()).toContain('account');    // unset account
    c.setCategoryAccount(0, 'a1');
    expect(c.categoryValidation()).toBeNull();
  });
});
