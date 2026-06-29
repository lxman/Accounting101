import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { environment } from './environment';

describe('authInterceptor', () => {
  let http: HttpClient;
  let ctrl: HttpTestingController;

  beforeEach(() => {
    environment.devToken = '';
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

  it('attaches the bearer token when set', () => {
    environment.devToken = 'tok123';
    http.get('/x').subscribe();
    const r = ctrl.expectOne('/x');
    expect(r.request.headers.get('Authorization')).toBe('Bearer tok123');
    r.flush({});
  });

  it('omits the header when no token', () => {
    environment.devToken = '';
    http.get('/y').subscribe();
    const r = ctrl.expectOne('/y');
    expect(r.request.headers.has('Authorization')).toBe(false);
    r.flush({});
  });
});
