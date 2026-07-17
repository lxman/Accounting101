import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { PeriodClose } from './period-close';
import { PeriodsService } from '../../core/periods/periods.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { PeriodStatus, PendingEntryRef } from '../../core/periods/periods';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000009';

async function boot(status: PeriodStatus, caps: string[] = ['gl.read', 'gl.close'], stubOverrides: Partial<Record<string, unknown>> = {}) {
  const stub = {
    status: vi.fn().mockReturnValue(of(status)),
    close: vi.fn().mockReturnValue(of({ asOf: '2026-06-30', openingBalances: [] })),
    closeYear: vi.fn().mockReturnValue(of({ closingEntry: null })),
    ...stubOverrides,
  };
  await TestBed.configureTestingModule({
    imports: [PeriodClose],
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideCapabilities(...caps), { provide: PeriodsService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(PeriodClose);
  f.detectChanges(); await f.whenStable(); f.detectChanges();
  return { f, stub };
}

describe('PeriodClose', () => {
  it('shows the closed-through date and fiscal year-end', async () => {
    const { f } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 });
    const text = (f.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('Closed through');
    expect(text).toContain('December');
    expect(text).toContain('Closed periods are final');
  });

  it('shows an empty state when nothing has been closed', async () => {
    const { f } = await boot({ closedThrough: null, fiscalYearEndMonth: 12 });
    expect((f.nativeElement as HTMLElement).textContent).toContain('No periods have been closed yet');
  });

  it('closes the next month after the closed-through date and refreshes', async () => {
    const { f, stub } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 });
    const el = f.nativeElement as HTMLElement;
    expect(el.textContent).toContain('June 2026');
    const btn = [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Close June 2026'))!;
    btn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable();
    expect(stub.close).toHaveBeenCalledWith('2026-06-30');
    expect(stub.status).toHaveBeenCalledTimes(2); // initial load + refresh after close
  });

  it('hides the close controls without gl.close', async () => {
    const { f } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 }, ['gl.read']);
    const el = f.nativeElement as HTMLElement;
    expect([...el.querySelectorAll('button')].some(b => b.textContent!.includes('Close June 2026'))).toBe(false);
  });

  function conflict(extensions: Record<string, unknown>) {
    return { error: { detail: 'Conflict.', ...extensions }, status: 409 };
  }

  it('lists blockers as journal links and retries the same close', async () => {
    const blockers: PendingEntryRef[] = [
      { entryId: 'e1', reference: 'ACCR-05', effectiveDate: '2026-06-30', type: 'Journal' },
      { entryId: 'e2', reference: 'BANK-05', effectiveDate: '2026-06-28', type: 'Journal' },
    ];
    const close = vi.fn()
      .mockReturnValueOnce(throwError(() => conflict({ blockers })))
      .mockReturnValueOnce(of({ asOf: '2026-06-30', openingBalances: [] }));
    const { f, stub } = await boot({ closedThrough: '2026-05-31', fiscalYearEndMonth: 12 }, ['gl.read', 'gl.close'], { close });
    const el = f.nativeElement as HTMLElement;

    [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Close June 2026'))!
      .dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable(); f.detectChanges();

    expect(el.textContent).toContain('ACCR-05');
    const link = [...el.querySelectorAll('a')].find(a => a.getAttribute('href')?.includes('/journal/e1'));
    expect(link).toBeTruthy();

    [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Retry close'))!
      .dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable();
    expect(close).toHaveBeenNthCalledWith(1, '2026-06-30');
    expect(close).toHaveBeenNthCalledWith(2, '2026-06-30'); // retry re-issues the same asOf
    expect(stub.status).toHaveBeenCalledTimes(2); // initial + refresh after the successful retry
  });

  it('shows the year-end affordance when the next period is the fiscal year-end', async () => {
    const { f, stub } = await boot({ closedThrough: '2026-11-30', fiscalYearEndMonth: 12 });
    const el = f.nativeElement as HTMLElement;
    expect([...el.querySelectorAll('button')].some(b => b.textContent!.includes('Close December 2026'))).toBe(false);
    const yeBtn = [...el.querySelectorAll('button')].find(b => b.textContent!.includes('Run year-end close'))!;
    expect(yeBtn).toBeTruthy();
    yeBtn.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable();
    expect(stub.closeYear).toHaveBeenCalledWith('2026-12-31');
  });
});
