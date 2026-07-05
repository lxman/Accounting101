import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { FixedAssetsShell } from './fixed-assets-shell';

describe('FixedAssetsShell', () => {
  it('renders the three tabs', () => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection(), provideRouter([])] });
    const f = TestBed.createComponent(FixedAssetsShell);
    f.detectChanges();
    const el = f.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="tab-assets"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="tab-runs"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="tab-disposals"]')).toBeTruthy();
  });
});
