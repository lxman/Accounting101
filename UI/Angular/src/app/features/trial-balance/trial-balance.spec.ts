import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { TrialBalance } from './trial-balance';
import { TrialBalanceService } from '../../core/trial-balance/trial-balance.service';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { TrialBalanceResponse } from '../../core/trial-balance/trial-balance';
import { formatMoney } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000005';

// Test data: two positive accounts (→ Debit), one negative (→ Credit).
// Debit total = 500 + 300 = 800; Credit total = 800 → they foot equal.
const mockTb: TrialBalanceResponse = {
  asOf: '2025-12-31',
  accounts: [
    { accountId: 'a1', balance: 500 },
    { accountId: 'a2', balance: 300 },
    { accountId: 'a3', balance: -800 },
  ],
};

const labelMap: Record<string, string> = {
  a1: '1001 Cash',
  a2: '1200 Accounts Receivable',
  a3: '4000 Revenue',
};

function makeAccountsStub() {
  const accts = signal([
    { id: 'a1', number: '1001', name: 'Cash' },
    { id: 'a2', number: '1200', name: 'Accounts Receivable' },
    { id: 'a3', number: '4000', name: 'Revenue' },
  ]);
  return {
    accounts: accts.asReadonly(),
    load: vi.fn(),
    label: (id: string) => labelMap[id] ?? id,
  };
}

describe('TrialBalance component', () => {
  let tbStub: { get: ReturnType<typeof vi.fn> };
  let accountsStub: ReturnType<typeof makeAccountsStub>;

  beforeEach(async () => {
    tbStub = { get: vi.fn().mockReturnValue(of(mockTb)) };
    accountsStub = makeAccountsStub();

    await TestBed.configureTestingModule({
      imports: [TrialBalance],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: TrialBalanceService, useValue: tbStub },
        { provide: AccountsService, useValue: accountsStub },
      ],
    }).compileComponents();

    const clientCtx = TestBed.inject(ClientContextService);
    clientCtx.select(clientId);
  });

  async function render() {
    const fixture = TestBed.createComponent(TrialBalance);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('renders a row for each account in the trial balance', async () => {
    const fixture = await render();
    const rows = (fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr');
    expect(rows.length).toBe(3);
  });

  it('places positive balance in the Debit column', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    const debitCells = el.querySelectorAll('[data-testid="debit-cell"]');
    // First two rows have positive balances → should show in debit column
    expect(debitCells[0].textContent?.trim()).toBe(
      formatMoney(500, 'USD', DEFAULT_FORMAT_PROFILE),
    );
    expect(debitCells[1].textContent?.trim()).toBe(
      formatMoney(300, 'USD', DEFAULT_FORMAT_PROFILE),
    );
    // Third row (negative) → debit cell should be blank
    expect(debitCells[2].textContent?.trim()).toBe('');
  });

  it('places negative balance (absolute value) in the Credit column', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    const creditCells = el.querySelectorAll('[data-testid="credit-cell"]');
    // First two rows (positive) → credit cells are blank
    expect(creditCells[0].textContent?.trim()).toBe('');
    expect(creditCells[1].textContent?.trim()).toBe('');
    // Third row has balance=-800 → credit cell shows 800 (absolute value)
    expect(creditCells[2].textContent?.trim()).toBe(
      formatMoney(800, 'USD', DEFAULT_FORMAT_PROFILE),
    );
  });

  it('joins account labels from AccountsService', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('1001 Cash');
    expect(el.textContent).toContain('1200 Accounts Receivable');
    expect(el.textContent).toContain('4000 Revenue');
  });

  it('column totals foot equal (debit total = credit total)', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    const debitTotal = el.querySelector('[data-testid="debit-total"]')?.textContent?.trim();
    const creditTotal = el.querySelector('[data-testid="credit-total"]')?.textContent?.trim();
    // Both should show $800.00
    expect(debitTotal).toBe(formatMoney(800, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true }));
    expect(creditTotal).toBe(formatMoney(800, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true }));
    expect(debitTotal).toBe(creditTotal);
  });

  it('does not show the out-of-balance indicator when balanced', async () => {
    const fixture = await render();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="out-of-balance"]')).toBeNull();
  });

  it('re-queries when asOf signal changes', async () => {
    const fixture = await render();
    const callsBefore = (tbStub.get as ReturnType<typeof vi.fn>).mock.calls.length;

    fixture.componentInstance.asOf.set('2024-06-30');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect((tbStub.get as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(
      callsBefore,
    );
    const lastArg = (tbStub.get as ReturnType<typeof vi.fn>).mock.calls.at(-1)?.[0];
    expect(lastArg).toBe('2024-06-30');
  });
});
