import { ChangeDetectionStrategy, Component, effect, input, output, signal } from '@angular/core';
import { HlmInputImports } from '@spartan-ng/helm/input';

/**
 * Money input: a `$`-adorned text field (no native number spinner), decimal keypad,
 * 2-decimal formatting on blur. Emits a `number` via `valueChange`. USD-only.
 */
@Component({
  selector: 'app-currency-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  imports: [...HlmInputImports],
  template: `
    <div class="relative">
      <span class="absolute left-2 top-1/2 -translate-y-1/2 text-muted-foreground text-sm pointer-events-none">$</span>
      <input hlmInput type="text" inputmode="decimal"
             class="w-full text-right tabular-nums ps-5"
             [attr.aria-label]="ariaLabel()"
             [value]="display()"
             (focus)="focused = true"
             (input)="onInput($any($event.target).value)"
             (blur)="onBlur()" />
    </div>
  `,
})
export class CurrencyInput {
  readonly value = input<number>(0);
  readonly ariaLabel = input<string>('Amount');
  readonly valueChange = output<number>();

  protected readonly display = signal('');
  // Plain flag (not a signal): while the user is editing we must not reformat under their cursor.
  protected focused = false;

  constructor() {
    // Reflect external value changes (e.g. edit-load), but never clobber an active edit.
    effect(() => {
      const v = this.value();
      if (!this.focused) this.display.set(this.format(v));
    });
  }

  onInput(raw: string): void {
    this.focused = true;
    this.display.set(raw);
    this.valueChange.emit(this.parse(raw));
  }

  onBlur(): void {
    this.focused = false;
    this.display.set(this.format(this.parse(this.display())));
  }

  private parse(s: string): number {
    const n = parseFloat(String(s).replace(/[^0-9.\-]/g, ''));
    return Number.isFinite(n) ? n : 0;
  }

  private format(n: number): string {
    return (Number.isFinite(n) ? n : 0).toFixed(2);
  }
}
