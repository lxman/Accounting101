import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { PeriodClose } from './period-close';
import { PeriodsService } from '../../core/periods/periods.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';
import { PeriodStatus } from '../../core/periods/periods';

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
});
