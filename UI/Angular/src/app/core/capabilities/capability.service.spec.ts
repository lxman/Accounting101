import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilityService } from './capability.service';
import { ClientContextService } from '../client/client-context.service';
import { DevIdentityService } from '../api/dev-identity.service';
import { environment } from '../api/environment';

describe('CapabilityService', () => {
  let http: HttpTestingController;
  let svc: CapabilityService;
  let client: ClientContextService;
  let identity: DevIdentityService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(ClientContextService);
    identity = TestBed.inject(DevIdentityService);
    svc = TestBed.inject(CapabilityService);
  });

  afterEach(() => http.verify());

  function tick(): void {
    TestBed.flushEffects?.();
  }

  function flush(caps: string[], roles: string[] = [], deploymentAdmin = false) {
    const clientId = client.clientId();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/${clientId}/me/capabilities`);
    req.flush({ capabilities: caps, roles, deploymentAdmin });
  }

  it('starts empty with no client selected', () => {
    expect(svc.capabilities().size).toBe(0);
    expect(svc.hasArea('gl')).toBe(false);
  });

  it('fetches and exposes capabilities when a client is selected', () => {
    client.select('c1');
    tick(); // let the reactive effect emit
    flush(['gl.read', 'ar.read', 'ar.write'], ['ArClerk']);
    expect(svc.has('ar.write')).toBe(true);
    expect(svc.hasArea('ar')).toBe(true);
    expect(svc.hasArea('ap')).toBe(false);
    expect(svc.roles()).toEqual(['ArClerk']);
  });

  it('re-fetches when the acting identity changes', () => {
    client.select('c1');
    tick();
    flush(['gl.read']); // first identity
    identity.use(identity.identities[1].sub);
    tick();
    flush(['gl.read', 'gl.post', 'ar.write'], ['Controller']); // second identity
    expect(svc.hasArea('ar')).toBe(true);
    expect(svc.has('gl.post')).toBe(true);
  });

  it('treats a 403 as an empty capability set', () => {
    client.select('c1');
    tick();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/me/capabilities`);
    req.flush('nope', { status: 403, statusText: 'Forbidden' });
    expect(svc.capabilities().size).toBe(0);
  });

  it('loaded is false before the first response and true after', () => {
    expect(svc.loaded()).toBe(false);
    client.select('c1');
    TestBed.flushEffects?.();
    flush(['gl.read']);
    expect(svc.loaded()).toBe(true);
  });

  it('loaded becomes true with no client (resolves empty)', () => {
    TestBed.flushEffects?.();
    expect(svc.loaded()).toBe(true);
    expect(svc.capabilities().size).toBe(0);
  });

  it('reload() refetches /me/capabilities without a key change', () => {
    client.select('c1');
    tick();
    const first = http.expectOne((r) => r.url.endsWith('/me/capabilities'));
    first.flush({ capabilities: ['ar.read'], roles: [], deploymentAdmin: false });
    tick();
    expect(svc.has('ar.read')).toBe(true);

    // Now reload() must trigger a brand-new GET to the same URL.
    svc.reload();
    tick();
    const second = http.expectOne((r) => r.url.endsWith('/me/capabilities'));
    second.flush({ capabilities: ['ar.read', 'ar.write'], roles: [], deploymentAdmin: false });
    tick();
    expect(svc.has('ar.write')).toBe(true);
  });
});
