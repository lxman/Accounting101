import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { DevIdentityService } from './dev-identity.service';
import { encodeDevToken } from './dev-token';
import { environment } from './environment';

describe('authInterceptor', () => {
  let http: HttpClient; let ctrl: HttpTestingController; let ids: DevIdentityService;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient); ctrl = TestBed.inject(HttpTestingController); ids = TestBed.inject(DevIdentityService);
  });
  afterEach(() => ctrl.verify());

  it('sets a DevToken for the active (clerk) identity', () => {
    http.get('/x').subscribe();
    const req = ctrl.expectOne('/x');
    const expected = encodeDevToken({ sub: environment.devClerk.sub, name: environment.devClerk.name, claims: environment.devClerk.claims });
    expect(req.request.headers.get('Authorization')).toBe(`DevToken ${expected}`);
    req.flush({});
  });

  it('re-mints the token after switching identity', () => {
    ids.use(environment.devApprover.sub);
    http.get('/y').subscribe();
    const req = ctrl.expectOne('/y');
    const expected = encodeDevToken({ sub: environment.devApprover.sub, name: environment.devApprover.name, claims: environment.devApprover.claims });
    expect(req.request.headers.get('Authorization')).toBe(`DevToken ${expected}`);
    req.flush({});
  });
});
