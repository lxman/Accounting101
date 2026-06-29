import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection()],
    });
    localStorage.clear();
    document.documentElement.classList.remove('dark');
  });

  it('defaults to light', () => {
    const s = TestBed.inject(ThemeService);
    expect(s.preference()).toBe('light');
    expect(s.effective()).toBe('light');
  });

  it('set() persists and updates effective + the .dark class', () => {
    const s = TestBed.inject(ThemeService);
    s.set('dark');
    expect(s.preference()).toBe('dark');
    expect(s.effective()).toBe('dark');
    expect(localStorage.getItem('a101.theme')).toBe('dark');
    TestBed.flushEffects?.();
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('removes the .dark class when set back to light', () => {
    const s = TestBed.inject(ThemeService);
    s.set('dark');
    TestBed.flushEffects?.();
    s.set('light');
    TestBed.flushEffects?.();
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });

  it('system preference resolves through effective()', () => {
    localStorage.setItem('a101.theme', 'system');
    const s = TestBed.inject(ThemeService);
    expect(s.preference()).toBe('system');
    expect(['light', 'dark']).toContain(s.effective());
  });
});
