import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApprovalQueue } from './approval-queue';
import { ClientContextService } from '../../core/client/client-context.service';
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

  it('lists pending entries and marks the active identity\'s own entry as not approvable', () => {
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
    // active identity defaults to the clerk → E1 is theirs (not approvable), E2 is approvable
    expect(f.componentInstance.approvableById()['E1']).toBe(false);
    expect(f.componentInstance.approvableById()['E2']).toBe(true);
  });
});
