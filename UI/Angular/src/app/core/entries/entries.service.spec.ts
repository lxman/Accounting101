import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EntriesService } from './entries.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';
import { PagedResponse } from '../api/paged-response';
import { EntryResponse } from './entry';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000002';

const mockEntry: EntryResponse = {
  id: 'entry-1',
  sequenceNumber: 1,
  effectiveDate: '2025-01-15',
  type: 'Journal',
  status: 'Approved',
  posting: 'Posted',
  lineCount: 2,
  supersedes: null,
  supersededBy: null,
  reversalOf: null,
  reversedBy: null,
  lines: [],
  sourceRef: null,
  sourceType: null,
  reference: null,
  memo: 'Test entry',
  viaModule: null,
};

const mockPage: PagedResponse<EntryResponse> = {
  items: [mockEntry],
  total: 1,
  skip: 0,
  limit: 50,
};

describe('EntriesService', () => {
  let service: EntriesService;
  let ctrl: HttpTestingController;
  let clientCtx: ClientContextService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(EntriesService);
    ctrl = TestBed.inject(HttpTestingController);
    clientCtx = TestBed.inject(ClientContextService);
    clientCtx.select(clientId);
  });

  afterEach(() => ctrl.verify());

  it('calls GET /clients/{id}/entries with skip and limit', () => {
    service.listPaged({ skip: 0, limit: 50 }).subscribe();
    const req = ctrl.expectOne(r =>
      r.url === `${environment.apiBaseUrl}/clients/${clientId}/entries` &&
      r.params.get('skip') === '0' &&
      r.params.get('limit') === '50',
    );
    expect(req.request.method).toBe('GET');
    req.flush(mockPage);
  });

  it('includes posting param when provided', () => {
    service.listPaged({ posting: 'Posted', skip: 0, limit: 50 }).subscribe();
    const req = ctrl.expectOne(r =>
      r.url === `${environment.apiBaseUrl}/clients/${clientId}/entries` &&
      r.params.get('posting') === 'Posted',
    );
    req.flush(mockPage);
  });

  it('omits posting param when undefined', () => {
    service.listPaged({ skip: 0, limit: 50 }).subscribe();
    const req = ctrl.expectOne(r =>
      r.url === `${environment.apiBaseUrl}/clients/${clientId}/entries`,
    );
    expect(req.request.params.has('posting')).toBe(false);
    req.flush(mockPage);
  });

  it('passes the PagedResponse envelope through', () => {
    let result: PagedResponse<EntryResponse> | undefined;
    service.listPaged({ posting: 'Posted', skip: 0, limit: 50 }).subscribe(r => (result = r));
    ctrl.expectOne(r =>
      r.url === `${environment.apiBaseUrl}/clients/${clientId}/entries`,
    ).flush(mockPage);
    expect(result).toEqual(mockPage);
  });

  it('does not send an order param', () => {
    service.listPaged({ skip: 0, limit: 50 }).subscribe();
    const req = ctrl.expectOne(r =>
      r.url === `${environment.apiBaseUrl}/clients/${clientId}/entries`,
    );
    expect(req.request.params.has('order')).toBe(false);
    req.flush(mockPage);
  });
});
