import { Injectable, signal, computed, effect } from '@angular/core';

export type ThemePreference = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'a101.theme';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly preference = signal<ThemePreference>(this.load());
  private readonly system = signal<'light' | 'dark'>(this.systemPrefersDark() ? 'dark' : 'light');
  readonly effective = computed<'light' | 'dark'>(() =>
    this.preference() === 'system' ? this.system() : (this.preference() as 'light' | 'dark'));

  constructor() {
    const mq = window.matchMedia?.('(prefers-color-scheme: dark)');
    mq?.addEventListener('change', e => this.system.set(e.matches ? 'dark' : 'light'));
    effect(() => document.documentElement.classList.toggle('dark', this.effective() === 'dark'));
  }

  set(pref: ThemePreference): void {
    this.preference.set(pref);
    localStorage.setItem(STORAGE_KEY, pref);
  }

  private load(): ThemePreference {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'light' || v === 'dark' || v === 'system' ? v : 'light';
  }

  private systemPrefersDark(): boolean {
    return window.matchMedia?.('(prefers-color-scheme: dark)')?.matches ?? false;
  }
}
