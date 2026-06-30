import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { InvoiceStatusBadge } from './invoice-status-badge';

describe('InvoiceStatusBadge', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] }));
  it('renders Draft / Issued / Void with matching testids', () => {
    for (const [s, tid] of [['Draft','badge-draft'],['Issued','badge-issued'],['Void','badge-void']] as const) {
      const f = TestBed.createComponent(InvoiceStatusBadge);
      f.componentRef.setInput('status', s); f.detectChanges();
      expect(f.nativeElement.querySelector(`[data-testid=${tid}]`)).toBeTruthy();
      expect(f.nativeElement.textContent).toContain(s);
    }
  });
});
