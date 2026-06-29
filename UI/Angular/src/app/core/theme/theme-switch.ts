import { Component, ChangeDetectionStrategy, inject } from '@angular/core';
import { ThemeService, ThemePreference } from './theme.service';

@Component({
  selector: 'app-theme-switch',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="inline-flex rounded-lg border border-border overflow-hidden">
      @for (opt of options; track opt.value) {
        <button
          type="button"
          class="px-2.5 py-1 text-xs transition-colors"
          [class.bg-primary]="theme.preference() === opt.value"
          [class.text-primary-foreground]="theme.preference() === opt.value"
          (click)="theme.set(opt.value)"
          [attr.aria-pressed]="theme.preference() === opt.value">{{ opt.label }}</button>
      }
    </div>`,
})
export class ThemeSwitch {
  protected readonly theme = inject(ThemeService);
  protected readonly options: { value: ThemePreference; label: string }[] = [
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
    { value: 'system', label: 'Auto' },
  ];
}
