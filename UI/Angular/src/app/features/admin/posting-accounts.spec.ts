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
});
