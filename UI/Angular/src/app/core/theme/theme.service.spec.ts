import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
    localStorage.clear();
    document.documentElement.removeAttribute('data-theme');
  });

  it('defaults to light', () => {
    const s = TestBed.inject(ThemeService);
    expect(s.preference()).toBe('light');
    expect(s.effective()).toBe('light');
  });

  it('set() persists and updates effective + the data-theme attribute', () => {
    const s = TestBed.inject(ThemeService);
    s.set('dark');
    expect(s.preference()).toBe('dark');
    expect(s.effective()).toBe('dark');
    expect(localStorage.getItem('a101.theme')).toBe('dark');
    TestBed.flushEffects?.();
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('system preference resolves through effective()', () => {
    localStorage.setItem('a101.theme', 'system');
    const s = TestBed.inject(ThemeService);
    expect(s.preference()).toBe('system');
    expect(['light', 'dark']).toContain(s.effective());
  });
});
