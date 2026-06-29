import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { environment } from './environment';
import { encodeDevToken } from './dev-token';

describe('authInterceptor', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;
  const devUserId = '00000000-0000-0000-0000-000000000001';

  beforeEach(() => {
    environment.devUserId = devUserId;
    environment.devUserName = 'Dev User';
    environment.devClaims = [{ type: 'role', value: 'Controller' }, { type: 'admin', value: 'true' }];
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    ctrl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctrl.verify());

  it('attaches the DevToken header when devUserId is set', () => {
    environment.devUserId = devUserId;
    http.get('/x').subscribe();
    const r = ctrl.expectOne('/x');
    const expected = `DevToken ${encodeDevToken({ sub: devUserId, name: 'Dev User', claims: environment.devClaims })}`;
    expect(r.request.headers.get('Authorization')).toBe(expected);
    r.flush({});
  });

  it('omits the header when devUserId is empty', () => {
    environment.devUserId = '';
    http.get('/y').subscribe();
    const r = ctrl.expectOne('/y');
    expect(r.request.headers.has('Authorization')).toBe(false);
    r.flush({});
  });
});
