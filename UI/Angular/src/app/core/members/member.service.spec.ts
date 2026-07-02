import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MemberService } from './member.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';

describe('MemberService', () => {
  let http: HttpTestingController; let svc: MemberService; let client: ClientContextService;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(ClientContextService);
    svc = TestBed.inject(MemberService);
    client.select('c1');
  });
  afterEach(() => http.verify());

  it('lists members', () => {
    svc.list().subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('adds a member', () => {
    svc.add({ userId: 'u2', roles: ['Auditor'], capabilities: ['gl.read'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members`);
    expect(req.request.method).toBe('POST');
    req.flush({ userId: 'u2', roles: ['Auditor'], capabilities: ['gl.read'] });
  });

  it('sets a member', () => {
    svc.set('u2', { roles: ['Controller'], capabilities: ['gl.read', 'gl.post'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u2`);
    expect(req.request.method).toBe('PUT');
    req.flush({ userId: 'u2', roles: ['Controller'], capabilities: ['gl.read', 'gl.post'] });
  });

  it('removes a member', () => {
    svc.remove('u2').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/members/u2`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });

  it('fetches the catalog', () => {
    svc.catalog().subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capabilities/catalog`);
    expect(req.request.method).toBe('GET');
    req.flush({ capabilities: [], roles: [] });
  });
});
