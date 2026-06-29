import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TrialBalanceService } from './trial-balance.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { TrialBalanceResponse } from './trial-balance';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000004';

const mockResponse: TrialBalanceResponse = {
  asOf: '2025-12-31',
  accounts: [
    { accountId: 'id-1001', balance: 5000 },
    { accountId: 'id-4000', balance: -3000 },
  ],
};

describe('TrialBalanceService', () => {
  let service: TrialBalanceService;
  let ctrl: HttpTestingController;
  let clientCtx: ClientContextService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(TrialBalanceService);
    ctrl = TestBed.inject(HttpTestingController);
    clientCtx = TestBed.inject(ClientContextService);
    clientCtx.select(clientId);
  });

  afterEach(() => ctrl.verify());

  it('calls GET /clients/{id}/trial-balance without asOf when not provided', () => {
    service.get().subscribe();
    const req = ctrl.expectOne(
      r => r.url === `${environment.apiBaseUrl}/clients/${clientId}/trial-balance`,
    );
    expect(req.request.method).toBe('GET');
    expect(req.request.params.has('asOf')).toBe(false);
    req.flush(mockResponse);
  });

  it('includes asOf query param when provided', () => {
    service.get('2025-12-31').subscribe();
    const req = ctrl.expectOne(
      r =>
        r.url === `${environment.apiBaseUrl}/clients/${clientId}/trial-balance` &&
        r.params.get('asOf') === '2025-12-31',
    );
    expect(req.request.method).toBe('GET');
    req.flush(mockResponse);
  });

  it('passes the TrialBalanceResponse through', () => {
    let result: TrialBalanceResponse | undefined;
    service.get('2025-12-31').subscribe(r => (result = r));
    ctrl
      .expectOne(r => r.url === `${environment.apiBaseUrl}/clients/${clientId}/trial-balance`)
      .flush(mockResponse);
    expect(result).toEqual(mockResponse);
  });
});
