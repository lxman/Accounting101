import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { SubledgerReconciliations } from './subledger-reconciliations';
import { SubledgerService } from '../../core/subledger/subledger.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { SubledgerReconciliationsResponse } from '../../core/subledger/subledger';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000003';

const resp: SubledgerReconciliationsResponse = {
  asOf: null,
  lines: [
    { account: 'ar', number: '1200', name: 'Accounts Receivable', dimension: 'Customer',
      controlBalance: 160, subledgerTotal: 160, variance: 0, tiesOut: true },
    { account: 'inv', number: '1400', name: 'Inventory', dimension: 'Item',
      controlBalance: 300, subledgerTotal: 250, variance: 50, tiesOut: false },
  ],
};

async function boot(response = resp) {
  const stub = { reconciliations: vi.fn().mockReturnValue(of(response)), breakdown: vi.fn() };
  await TestBed.configureTestingModule({
    imports: [SubledgerReconciliations],
    providers: [provideZonelessChangeDetection(), { provide: SubledgerService, useValue: stub }],
  }).compileComponents();
  TestBed.inject(ClientContextService).select(clientId);
  const f = TestBed.createComponent(SubledgerReconciliations);
  f.detectChanges(); await f.whenStable(); f.detectChanges();
  return { f, stub };
}

describe('SubledgerReconciliations', () => {
  it('renders a row per line with balances and a ties-out / variance badge', async () => {
    const { f } = await boot();
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelectorAll('tbody tr').length).toBe(2);
    expect(el.textContent).toContain('Accounts Receivable');
    expect(el.textContent).toContain('160.00');
    expect(el.textContent).toContain('Ties out');
    expect(el.textContent).toContain('50.00');    // inventory variance
    expect(el.textContent).toContain('Variance');
  });

  it('shows an empty state when there are no dimensioned control accounts', async () => {
    const { f } = await boot({ asOf: null, lines: [] });
    expect((f.nativeElement as HTMLElement).textContent).toContain('No dimensioned control accounts');
  });
});
