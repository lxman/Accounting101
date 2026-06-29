import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuditService } from './audit.service';
import { AuditRecordResponse } from './audit';
import { ClientContextService } from '../client/client-context.service';

describe('AuditService', () => {
  let svc: AuditService; let ctrl: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(AuditService); ctrl = TestBed.inject(HttpTestingController);
    TestBed.inject(ClientContextService).select('C1');
  });
  afterEach(() => ctrl.verify());

  it('GETs /audit/{id}', () => {
    svc.entryAudit('E1').subscribe();
    const http = ctrl.expectOne('http://localhost:5000/clients/C1/audit/E1');
    expect(http.request.method).toBe('GET'); http.flush([]);
  });

  it('authorOf returns the userId of the Created record', () => {
    const recs: AuditRecordResponse[] = [
      { sequence: 2, action: 'Posted', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: 'U2', name: null, claims: [] } },
      { sequence: 1, action: 'Created', entryId: 'E1', entryVersion: 1, at: '', reason: null, actor: { userId: 'U1', name: 'Clerk', claims: [] } },
    ];
    expect(svc.authorOf(recs)).toBe('U1');
  });

  it('authorOf falls back to null when no Created record', () => {
    expect(svc.authorOf([])).toBeNull();
  });
});
