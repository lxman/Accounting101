import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { VerifyIntegrity } from './verify-integrity';
import { AuditService } from '../../core/audit/audit.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { AuditVerifyResponse } from '../../core/audit/audit';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000003';

async function boot(resp: AuditVerifyResponse) {
  const stub = { verify: vi.fn().mockReturnValue(of(resp)) };
  await TestBed.configureTestingModule({
    imports: [VerifyIntegrity],
    providers: [provideZonelessChangeDetection(), { provide: AuditService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(VerifyIntegrity);
  f.detectChanges();
  return f;
}

function clickCheck(f: { nativeElement: HTMLElement }) {
  const btn = [...f.nativeElement.querySelectorAll('button')].find(b => b.textContent?.includes('Check integrity'))!;
  btn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}

describe('VerifyIntegrity', () => {
  it('reports an intact chain', async () => {
    const f = await boot({ valid: true, recordCount: 42, headSequence: 42, failure: null, brokenAtSequence: null });
    clickCheck(f); f.detectChanges(); await f.whenStable(); f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain('intact');
    expect((f.nativeElement as HTMLElement).textContent).toContain('42');
  });

  it('humanizes a tampered record', async () => {
    const f = await boot({ valid: false, recordCount: 42, headSequence: 42, failure: 'HashMismatch', brokenAtSequence: 12 });
    clickCheck(f); f.detectChanges(); await f.whenStable(); f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain('Tampered record at sequence 12');
  });

  it('humanizes a truncated tail', async () => {
    const f = await boot({ valid: false, recordCount: 40, headSequence: 42, failure: 'TailTruncated', brokenAtSequence: 41 });
    clickCheck(f); f.detectChanges(); await f.whenStable(); f.detectChanges();
    expect((f.nativeElement as HTMLElement).textContent).toContain('deleted from the end');
  });
});
