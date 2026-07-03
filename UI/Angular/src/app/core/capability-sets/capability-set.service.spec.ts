import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CapabilitySetService } from './capability-set.service';
import { environment } from '../api/environment';

describe('CapabilitySetService', () => {
  let http: HttpTestingController;
  let svc: CapabilitySetService;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    svc = TestBed.inject(CapabilitySetService);
  });
  afterEach(() => http.verify());

  it('lists sets from the deployment-scoped route', () => {
    svc.list().subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets`);
    expect(req.request.method).toBe('GET');
    req.flush([]);
  });

  it('creates a set', () => {
    svc.create({ name: 'Warehouse', capabilities: ['gl.read'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Warehouse', capabilities: ['gl.read'] });
    req.flush({});
  });

  it('updates a set by id', () => {
    svc.update('s1', { name: 'Edited', capabilities: ['gl.read', 'gl.post'] }).subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(req.request.method).toBe('PUT');
    req.flush({});
  });

  it('deletes a set by id', () => {
    svc.remove('s1').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/capability-sets/s1`);
    expect(req.request.method).toBe('DELETE');
    req.flush(null);
  });
});
