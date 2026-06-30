import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { SettlementBadge } from './settlement-badge';

describe('SettlementBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }));
  it('renders Open / PartiallyPaid / Paid with matching testids', () => {
    for (const [s, tid, text] of [['Open','badge-open','Open'],['PartiallyPaid','badge-partial','Partial'],['Paid','badge-paid','Paid']] as const) {
      const f = TestBed.createComponent(SettlementBadge);
      f.componentRef.setInput('status', s); f.detectChanges();
      expect(f.nativeElement.querySelector(`[data-testid=${tid}]`)).toBeTruthy();
      expect(f.nativeElement.textContent).toContain(text);
    }
  });
});
