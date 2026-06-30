import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { PayablesShell } from './payables-shell';

describe('PayablesShell', () => {
  it('renders Bills and Vendors tabs', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideRouter([])],
    });
    const f = TestBed.createComponent(PayablesShell);
    f.detectChanges();
    const tabs = f.nativeElement.textContent;
    expect(tabs).toContain('Bills');
    expect(tabs).toContain('Vendors');
  });
});
