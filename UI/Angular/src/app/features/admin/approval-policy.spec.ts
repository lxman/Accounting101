import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { vi } from 'vitest';
import { ApprovalPolicyScreen } from './approval-policy';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { CapabilityService } from '../../core/capabilities/capability.service';
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

  it('reloads capabilities after a successful save so nav re-gates', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const caps = TestBed.inject(CapabilityService);
    const reloadSpy = vi.spyOn(caps, 'reload');
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson' });
    f.detectChanges();

    const c = f.componentInstance as ApprovalPolicyScreen;
    c.select('AutoApprove');
    c.save();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'AutoApprove' });

    expect(reloadSpy).toHaveBeenCalledTimes(1);
  });

  it('disables Auto-approve and shows a note when entries are pending', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson', pendingApprovalCount: 2 });
    f.detectChanges();

    const el = f.nativeElement as HTMLElement;
    const autoRadio = Array.from(el.querySelectorAll('input[type=radio]'))
      .find((r) => (r as HTMLInputElement).value === 'AutoApprove') as HTMLInputElement;
    expect(autoRadio.disabled).toBe(true);
    const note = el.querySelector('[data-testid=pending-note]');
    expect(note?.textContent).toContain('2 entries are');
    expect(el.querySelector('[data-testid=pending-note] a[href="/journal/approvals"]')).not.toBeNull();
  });

  it('enables Auto-approve and shows no note when nothing is pending', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'TwoPerson', pendingApprovalCount: 0 });
    f.detectChanges();

    const el = f.nativeElement as HTMLElement;
    const autoRadio = Array.from(el.querySelectorAll('input[type=radio]'))
      .find((r) => (r as HTMLInputElement).value === 'AutoApprove') as HTMLInputElement;
    expect(autoRadio.disabled).toBe(false);
    expect(el.querySelector('[data-testid=pending-note]')).toBeNull();
  });

  it('surfaces a 422 detail from a failed save', () => {
    seed(); http = TestBed.inject(HttpTestingController);
    const f = TestBed.createComponent(ApprovalPolicyScreen);
    f.detectChanges();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush({ mode: 'SelfApprove', pendingApprovalCount: 0 });
    f.detectChanges();

    const c = f.componentInstance as ApprovalPolicyScreen;
    c.select('AutoApprove');   // allowed here: count is 0
    c.save();
    http.expectOne(`${environment.apiBaseUrl}/clients/c1/approval-policy`).flush(
      { detail: 'Cannot enable auto-approve while 1 entry awaits approval. Clear the approval queue first.' },
      { status: 422, statusText: 'Unprocessable Entity' });
    f.detectChanges();

    expect((f.nativeElement as HTMLElement).textContent).toContain('Cannot enable auto-approve');
  });
});
