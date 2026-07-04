import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { capabilitySelfHealInterceptor } from './self-heal.interceptor';
import { CapabilityService } from './capability.service';
import { StubCapabilityService } from './capability.testing';

describe('capabilitySelfHealInterceptor', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;
  let caps: StubCapabilityService;

  beforeEach(() => {
    caps = new StubCapabilityService();
    vi.spyOn(caps, 'reload');
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([capabilitySelfHealInterceptor])),
        provideHttpClientTesting(),
        { provide: CapabilityService, useValue: caps },
      ],
    });
    http = TestBed.inject(HttpClient);
    ctrl = TestBed.inject(HttpTestingController);
  });
  afterEach(() => ctrl.verify());

  it('reloads capabilities on a 403 from a normal request', () => {
    http.get('/clients/c1/entries').subscribe({ next: () => {}, error: () => {} });
    ctrl.expectOne('/clients/c1/entries').flush('nope', { status: 403, statusText: 'Forbidden' });
    expect(caps.reload).toHaveBeenCalledTimes(1);
  });

  it('does NOT reload on a 403 from the capabilities fetch itself (no loop)', () => {
    http.get('/clients/c1/me/capabilities').subscribe({ next: () => {}, error: () => {} });
    ctrl.expectOne('/clients/c1/me/capabilities').flush('nope', { status: 403, statusText: 'Forbidden' });
    expect(caps.reload).not.toHaveBeenCalled();
  });

  it('does not reload on a non-403 error', () => {
    http.get('/clients/c1/entries').subscribe({ next: () => {}, error: () => {} });
    ctrl.expectOne('/clients/c1/entries').flush('boom', { status: 500, statusText: 'Server Error' });
    expect(caps.reload).not.toHaveBeenCalled();
  });
});
