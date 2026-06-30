import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApprovalQueue } from './approval-queue';
import { ClientContextService } from '../../core/client/client-context.service';
import { DevIdentityService } from '../../core/api/dev-identity.service';
import { environment } from '../../core/api/environment';

describe('ApprovalQueue', () => {
  let ctrl: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  });
  afterEach(() => ctrl.verify());

  it('lists pending entries and marks own entry as own, other entry as approvable', () => {
    const f = TestBed.createComponent(ApprovalQueue); f.detectChanges();
    const page = ctrl.expectOne(r => r.url.includes('/clients/C1/entries') && r.params.get('posting') === 'PendingApproval');
    page.flush({ items: [
      { id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: 'mine', supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
      { id: 'E2', sequenceNumber: 2, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: 'theirs', supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
    ], total: 2, skip: 0, limit: 50 });
    f.detectChanges();
    // audit fetched per row to resolve the author
    const a1 = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1');
    a1.flush([{ sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: environment.devClerk.sub, name: 'Clerk', claims: [] } }]);
    const a2 = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E2');
    a2.flush([{ sequence: 1, action: 'Created', entryId: 'E2', entryVersion: 1, at: '', reason: null, actor: { userId: environment.devApprover.sub, name: 'Other', claims: [] } }]);
    f.detectChanges();
    // active identity defaults to the clerk → E1 is theirs ('own'), E2 is approvable
    expect(f.componentInstance.cueById()['E1']).toBe('own');
    expect(f.componentInstance.cueById()['E2']).toBe('approvable');
  });

  it('flips the cue when the active identity changes', () => {
    const f = TestBed.createComponent(ApprovalQueue); f.detectChanges();
    ctrl.expectOne(r => r.params.get('posting') === 'PendingApproval').flush({ items: [
      { id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: null, supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
    ], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush([
      { sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: environment.devClerk.sub, name: 'C', claims: [] } }]);
    f.detectChanges();
    expect(f.componentInstance.cueById()['E1']).toBe('own');     // active = clerk = author
    TestBed.inject(DevIdentityService).use(environment.devApprover.sub);
    f.detectChanges();
    expect(f.componentInstance.cueById()['E1']).toBe('approvable');
  });

  it('treats an empty-array audit (HTTP 200, zero records) as unknown cue', () => {
    const f = TestBed.createComponent(ApprovalQueue); f.detectChanges();
    ctrl.expectOne(r => r.params.get('posting') === 'PendingApproval').flush({ items: [
      { id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: null, supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
    ], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    // HTTP 200 but zero audit records → authorOf([]) returns null → cue must be 'unknown'
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush([]);
    f.detectChanges();
    expect(f.componentInstance.cueById()['E1']).toBe('unknown');
  });

  it('degrades a single audit failure to unknown without breaking other rows', () => {
    const f = TestBed.createComponent(ApprovalQueue); f.detectChanges();
    ctrl.expectOne(r => r.params.get('posting') === 'PendingApproval').flush({ items: [
      { id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: null, supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
      { id: 'E2', sequenceNumber: 2, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: null, supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
    ], total: 2, skip: 0, limit: 50 });
    f.detectChanges();
    // E1 audit fails — should degrade to 'unknown'
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush('error', { status: 404, statusText: 'Not Found' });
    // E2 audit succeeds — should still resolve to 'approvable'
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E2').flush([
      { sequence: 1, action: 'Created', entryId: 'E2', entryVersion: 1, at: '', reason: null, actor: { userId: environment.devApprover.sub, name: 'Other', claims: [] } }]);
    f.detectChanges();
    expect(f.componentInstance.cueById()['E1']).toBe('unknown');
    expect(f.componentInstance.cueById()['E2']).toBe('approvable');
  });

  it('clicking a row navigates to the entry detail (whole-row click)', () => {
    const f = TestBed.createComponent(ApprovalQueue); f.detectChanges();
    ctrl.expectOne(r => r.params.get('posting') === 'PendingApproval').flush({ items: [
      { id: 'E1', sequenceNumber: 1, effectiveDate: '2026-06-29', type: 'Standard', status: 'Active', posting: 'PendingApproval', lineCount: 2, lines: [], memo: null, supersedes: null, supersededBy: null, reversalOf: null, reversedBy: null },
    ], total: 1, skip: 0, limit: 50 });
    f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1').flush([]);
    f.detectChanges();
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    f.nativeElement.querySelector('tbody tr').click();
    expect(nav).toHaveBeenCalledWith(['/journal', 'E1']);
  });
});
