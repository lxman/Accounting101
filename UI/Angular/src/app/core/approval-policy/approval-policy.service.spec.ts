import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApprovalPolicyService } from './approval-policy.service';
import { ClientContextService } from '../client/client-context.service';
import { environment } from '../api/environment';

describe('ApprovalPolicyService', () => {
  let http: HttpTestingController;
  let service: ApprovalPolicyService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    TestBed.inject(ClientContextService).select('c1');
    http = TestBed.inject(HttpTestingController);
    service = TestBed.inject(ApprovalPolicyService);
  });
  afterEach(() => http.verify());

  it('GETs the current policy', () => {
    let got: string | undefined;
    service.get().subscribe((p) => (got = p.mode));
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'AutoApprove' });
    expect(got).toBe('AutoApprove');
  });

  it('PUTs the chosen mode', () => {
    service.set('SelfApprove').subscribe();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ mode: 'SelfApprove' });
    req.flush({ mode: 'SelfApprove' });
  });
});
