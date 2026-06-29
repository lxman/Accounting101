import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { BalanceSheet } from './balance-sheet';
import { StatementsService } from '../../core/statements/statements.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { BalanceSheetResponse } from '../../core/statements/statement';
import { formatMoney } from '../../core/format/money-formatter';
import { DEFAULT_FORMAT_PROFILE } from '../../core/format/format-profile';

const clientId = 'aaaaaaaa-0000-0000-0000-000000000007';

const mockBalancedResponse: BalanceSheetResponse = {
  asOf: '2025-12-31',
  assets: {
    title: 'Assets',
    lines: [
      { accountId: 'a1', number: '1001', name: 'Cash', amount: 8000 },
      { accountId: 'a2', number: '1200', name: 'Accounts Receivable', amount: 2000 },
    ],
    total: 10000,
  },
  liabilities: {
    title: 'Liabilities',
    lines: [{ accountId: 'l1', number: '2000', name: 'Accounts Payable', amount: 4000 }],
    total: 4000,
  },
  equity: {
    title: 'Equity',
    lines: [{ accountId: 'e1', number: '3000', name: 'Retained Earnings', amount: 6000 }],
    total: 6000,
  },
  totalAssets: 10000,
  totalLiabilitiesAndEquity: 10000,
  isBalanced: true,
};

const mockUnbalancedResponse: BalanceSheetResponse = {
  ...mockBalancedResponse,
  totalLiabilitiesAndEquity: 9500,
  isBalanced: false,
};

describe('BalanceSheet component', () => {
  let svcStub: { balanceSheet: ReturnType<typeof vi.fn> };

  async function setup(response: BalanceSheetResponse) {
    svcStub = { balanceSheet: vi.fn().mockReturnValue(of(response)) };

    await TestBed.configureTestingModule({
      imports: [BalanceSheet],
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        { provide: StatementsService, useValue: svcStub },
      ],
    }).compileComponents();

    const clientCtx = TestBed.inject(ClientContextService);
    clientCtx.select(clientId);

    const fixture = TestBed.createComponent(BalanceSheet);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('renders the three sections (Assets, Liabilities, Equity)', async () => {
    const fixture = await setup(mockBalancedResponse);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.textContent).toContain('Assets');
    expect(el.textContent).toContain('Liabilities');
    expect(el.textContent).toContain('Equity');
  });

  it('shows totalAssets and totalLiabilitiesAndEquity with $ symbol', async () => {
    const fixture = await setup(mockBalancedResponse);
    const el = fixture.nativeElement as HTMLElement;
    const totalAssets = el.querySelector('[data-testid="total-assets"]')?.textContent?.trim();
    const totalLE = el.querySelector('[data-testid="total-liabilities-equity"]')?.textContent?.trim();
    expect(totalAssets).toBe(formatMoney(10000, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true }));
    expect(totalLE).toBe(formatMoney(10000, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: true }));
  });

  it('shows Balanced badge (teal style) when isBalanced is true', async () => {
    const fixture = await setup(mockBalancedResponse);
    const el = fixture.nativeElement as HTMLElement;
    const badge = el.querySelector('[data-testid="balanced-badge"]');
    expect(badge).not.toBeNull();
    expect(el.querySelector('[data-testid="unbalanced-badge"]')).toBeNull();
  });

  it('shows Out of Balance badge (destructive) when isBalanced is false', async () => {
    const fixture = await setup(mockUnbalancedResponse);
    const el = fixture.nativeElement as HTMLElement;
    const badge = el.querySelector('[data-testid="unbalanced-badge"]');
    expect(badge).not.toBeNull();
    expect(el.querySelector('[data-testid="balanced-badge"]')).toBeNull();
  });

  it('formats line amounts without $ symbol (plain)', async () => {
    const fixture = await setup(mockBalancedResponse);
    const el = fixture.nativeElement as HTMLElement;
    // Line items should appear without leading '$'
    const formatted = formatMoney(8000, 'USD', DEFAULT_FORMAT_PROFILE, { symbol: false });
    expect(el.textContent).toContain(formatted);
  });

  it('re-queries when asOf signal changes', async () => {
    const fixture = await setup(mockBalancedResponse);
    const callsBefore = svcStub.balanceSheet.mock.calls.length;

    fixture.componentInstance.asOf.set('2024-06-30');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(svcStub.balanceSheet.mock.calls.length).toBeGreaterThan(callsBefore);
    const lastArg = svcStub.balanceSheet.mock.calls.at(-1)?.[0];
    expect(lastArg).toBe('2024-06-30');
  });
});
