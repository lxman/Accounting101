import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';
import { AuditTrail } from './audit-trail';
import { AuditService } from '../../core/audit/audit.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { PagedResponse } from '../../core/api/paged-response';
import { AuditRecordResponse } from '../../core/audit/audit';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000003';

function rec(o: Partial<AuditRecordResponse> = {}): AuditRecordResponse {
  return { sequence: 1, action: 'Created', entryId: 'e1', entryVersion: 1, at: '2026-03-15T00:00:00Z',
    reason: null, actor: { userId: 'u1', name: 'Alice', claims: [] }, ...o };
}
const page: PagedResponse<AuditRecordResponse> = {
  items: [rec({ sequence: 1, entryId: 'e1' }), rec({ sequence: 2, action: 'AccountCreated', entryId: null })],
  total: 3, skip: 0, limit: 2,
};

async function boot(caps: string[] = ['audit.read', 'gl.read']) {
  const stub = { clientAudit: vi.fn().mockReturnValue(of(page)) };
  await TestBed.configureTestingModule({
    imports: [AuditTrail],
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideCapabilities(...caps),
      { provide: AuditService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(AuditTrail);
  f.detectChanges(); await f.whenStable(); f.detectChanges();
  return { f, stub };
}

describe('AuditTrail', () => {
  it('renders a row per record and the page count', async () => {
    const { f } = await boot();
    expect((f.nativeElement as HTMLElement).querySelectorAll('tbody tr').length).toBe(2);
    expect((f.nativeElement as HTMLElement).textContent).toContain('Page 1 of 2'); // total 3 / limit 2
    expect((f.nativeElement as HTMLElement).textContent).toContain('Alice');
  });

  it('drills a row with an entry to the journal entry when the user has gl.read', async () => {
    const { f } = await boot(['audit.read', 'gl.read']);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const rows = [...(f.nativeElement as HTMLElement).querySelectorAll('tbody tr')] as HTMLElement[];
    rows[0].dispatchEvent(new MouseEvent('click', { bubbles: true }));  // entryId e1
    rows[1].dispatchEvent(new MouseEvent('click', { bubbles: true }));  // entryId null → no nav
    expect(nav.mock.calls.map(c => c[0])).toEqual([['/journal', 'e1']]);
  });

  it('does not drill when the user lacks gl.read', async () => {
    const { f } = await boot(['audit.read']);
    const nav = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    ([...(f.nativeElement as HTMLElement).querySelectorAll('tbody tr')] as HTMLElement[])
      .forEach(r => r.dispatchEvent(new MouseEvent('click', { bubbles: true })));
    expect(nav).not.toHaveBeenCalled();
  });
});
