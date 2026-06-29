import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AccountsService } from './accounts.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { AccountResponse } from './account';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000001';

const mockAccounts: AccountResponse[] = [
  {
    id: 'id-1001', number: '1001', name: 'Cash', type: 'Asset', parentId: null,
    postable: true, requiredDimension: null, cashFlowActivity: null,
    isRetainedEarnings: false, active: true, normalSide: 'Debit', isTemporary: false,
  },
  {
    id: 'id-4000', number: '4000', name: 'Revenue', type: 'Revenue', parentId: null,
    postable: true, requiredDimension: null, cashFlowActivity: null,
    isRetainedEarnings: false, active: true, normalSide: 'Credit', isTemporary: true,
  },
];

describe('AccountsService', () => {
  let service: AccountsService;
  let ctrl: HttpTestingController;
  let clientCtx: ClientContextService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(AccountsService);
    ctrl = TestBed.inject(HttpTestingController);
    clientCtx = TestBed.inject(ClientContextService);
  });

  afterEach(() => ctrl.verify());

  it('issues GET /clients/{id}/accounts when a client is selected', () => {
    clientCtx.select(clientId);
    service.load();
    const req = ctrl.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/accounts`);
    expect(req.request.method).toBe('GET');
    req.flush(mockAccounts);
    expect(service.accounts().length).toBe(2);
  });

  it('populates accounts signal after flush', () => {
    clientCtx.select(clientId);
    service.load();
    ctrl.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/accounts`).flush(mockAccounts);
    expect(service.accounts()).toEqual(mockAccounts);
  });

  it('label returns "<number> <name>" for a known account', () => {
    clientCtx.select(clientId);
    service.load();
    ctrl.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/accounts`).flush(mockAccounts);
    expect(service.label('id-1001')).toBe('1001 Cash');
    expect(service.label('id-4000')).toBe('4000 Revenue');
  });

  it('label falls back to the id when unknown', () => {
    clientCtx.select(clientId);
    service.load();
    ctrl.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/accounts`).flush(mockAccounts);
    expect(service.label('unknown-id')).toBe('unknown-id');
  });

  it('byId computed map is keyed by account id', () => {
    clientCtx.select(clientId);
    service.load();
    ctrl.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/accounts`).flush(mockAccounts);
    expect(service.byId().get('id-1001')?.name).toBe('Cash');
  });

  it('no-ops load() when no client is selected', () => {
    service.load(); // clientId() is null
    ctrl.expectNone(`${environment.apiBaseUrl}/clients/${clientId}/accounts`);
    expect(service.accounts()).toEqual([]);
  });

  it('upsert PUTs the account and refreshes the cache', () => {
    const svc = TestBed.inject(AccountsService);
    const ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
    const a = { id: 'A1', number: '1000', name: 'Cash', type: 'Asset' as const, parentId: null,
      postable: true, requiredDimension: null, cashFlowActivity: 'Operating', isRetainedEarnings: false, active: true };
    let saved: AccountResponse | undefined;
    svc.upsert(a).subscribe(r => (saved = r));
    const req = ctrl.expectOne('http://localhost:5000/clients/C1/accounts/A1');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ number: '1000', name: 'Cash', type: 'Asset', parentId: null,
      postable: true, requiredDimension: null, cashFlowActivity: 'Operating', isRetainedEarnings: false, active: true });
    const resp = { ...a, normalSide: 'Debit', isRetainedEarnings: false, isTemporary: false } as AccountResponse;
    req.flush(resp);
    expect(saved).toEqual(resp);
    expect(svc.accounts().find(x => x.id === 'A1')).toEqual(resp);
  });
});
