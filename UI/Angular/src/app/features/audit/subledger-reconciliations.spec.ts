import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { of } from 'rxjs';
import { SubledgerReconciliations } from './subledger-reconciliations';
import { SubledgerService } from '../../core/subledger/subledger.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { SubledgerReconciliationsResponse, SubledgerResponse } from '../../core/subledger/subledger';

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

  it('expands a variance row to lazy-load its breakdown and shows the untagged remainder', async () => {
    const breakdown: SubledgerResponse = {
      dimension: 'Item', asOf: null,
      lines: [
        { accountId: 'inv', dimensionValue: 'i1', balance: 150, number: 'WIDGET', name: 'Widget' },
        { accountId: 'inv', dimensionValue: 'i2', balance: 100, number: 'GADGET', name: 'Gadget' },
      ],
    };
    const stub = { reconciliations: vi.fn().mockReturnValue(of(resp)), breakdown: vi.fn().mockReturnValue(of(breakdown)) };
    await TestBed.configureTestingModule({
      imports: [SubledgerReconciliations],
      providers: [provideZonelessChangeDetection(), { provide: SubledgerService, useValue: stub }],
    }).compileComponents();
    TestBed.inject(ClientContextService).select(clientId);
    const f = TestBed.createComponent(SubledgerReconciliations);
    f.detectChanges(); await f.whenStable(); f.detectChanges();

    const invRow = [...(f.nativeElement as HTMLElement).querySelectorAll('tbody tr')]
      .find(tr => tr.textContent!.includes('Inventory')) as HTMLElement;
    invRow.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable(); f.detectChanges();
    invRow.dispatchEvent(new MouseEvent('click', { bubbles: true }));   // toggle off then on again below — no re-fetch
    f.detectChanges();
    invRow.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    f.detectChanges(); await f.whenStable(); f.detectChanges();

    expect(stub.breakdown).toHaveBeenCalledTimes(1);                    // cached across expands
    expect(stub.breakdown).toHaveBeenCalledWith('inv', 'Item');
    const text = (f.nativeElement as HTMLElement).textContent!;
    expect(text).toContain('WIDGET');
    expect(text).toContain('150.00');
    expect(text).toContain('Untagged remainder');
    expect(text).toContain('50.00');   // the variance
  });
});
