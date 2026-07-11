import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApprovalPolicyScreen } from './approval-policy';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { ClientContextService } from '../../core/client/client-context.service';
import { environment } from '../../core/api/environment';

function seed() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(),
                provideHttpClientTesting(), provideCapabilities('admin.approvalPolicy')],
  });
  TestBed.inject(ClientContextService).select('c1');
}

describe('ApprovalPolicyScreen', () => {
  let http: HttpTestingController;
  afterEach(() => http.verify());

  it('loads the current mode and PUTs the chosen one', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson' });
    f.detectChanges();

    const c = f.componentInstance as ApprovalPolicyScreen;
    expect(c.selected()).toBe('TwoPerson');
    expect(c.options.length).toBe(3);

    c.select('AutoApprove');
    c.save();
    const req = http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ mode: 'AutoApprove' });
    req.flush({ mode: 'AutoApprove' });
    expect(c.saved()).toBe(true);
  });
});
