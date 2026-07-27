import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { DevIdentityService } from './dev-identity.service';
import { encodeDevToken } from './dev-token';
import { DEV_IDENTITIES } from './dev-identities';

/**
 * Asserts against DEV_IDENTITIES because that is what the interceptor encodes: it reads
 * DevIdentityService.active(), and the service's roster IS DEV_IDENTITIES. These tests previously
 * built their expectation from environment.devClerk/devApprover, which no production code reads —
 * the two drifted (sub …0001 is 'Dev Controller' here, 'Dev Clerk' there) and this file was the only
 * place the drift was observable, because every other spec uses those constants only for their .sub.
 *
 * Note the deployed books build replaces dev-identities.ts with a single owner identity; specs run
 * against the repo's roster, never that generated one. See the header of dev-identities.ts.
 */
describe('authInterceptor', () => {
  let http: HttpClient; let ctrl: HttpTestingController; let ids: DevIdentityService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient); ctrl = TestBed.inject(HttpTestingController); ids = TestBed.inject(DevIdentityService);
  });
  afterEach(() => ctrl.verify());

  it('sets a DevToken for the default active identity', () => {
    const first = DEV_IDENTITIES[0];                 // DevIdentityService defaults to identities[0]
    http.get('/x').subscribe();
    const req = ctrl.expectOne('/x');
    const expected = encodeDevToken({ sub: first.sub, name: first.name, claims: first.claims });
    expect(req.request.headers.get('Authorization')).toBe(`DevToken ${expected}`);
    req.flush({});
  });

  it('re-mints the token after switching identity', () => {
    const second = DEV_IDENTITIES[1];
    ids.use(second.sub);
    http.get('/y').subscribe();
    const req = ctrl.expectOne('/y');
    const expected = encodeDevToken({ sub: second.sub, name: second.name, claims: second.claims });
    expect(req.request.headers.get('Authorization')).toBe(`DevToken ${expected}`);
    req.flush({});
  });
});
