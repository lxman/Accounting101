import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { FiscalService } from '../../core/fiscal/fiscal.service';

interface MonthOption { value: number; label: string; }

@Component({
  selector: 'app-fiscal-settings',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective],
  template: `
    <h1 class="text-xl font-semibold mb-4">Fiscal settings</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    @if (saved()) { <p class="text-green-600 mb-2">Saved.</p> }
    <p class="text-sm text-muted-foreground mb-3">The month this client's fiscal year ends.</p>

    <label class="block">
      <span class="text-sm font-medium">Fiscal year-end month</span>
      <select class="mt-1 block w-64 rounded border border-border bg-background px-3 py-2 text-sm"
              [value]="selected()" (change)="select($event)" data-testid="fye-select">
        @for (m of months; track m.value) {
          <option [value]="m.value">{{ m.label }}</option>
        }
      </select>
    </label>

    <p class="text-xs text-muted-foreground mt-2 max-w-prose">Changing this affects future closes only.
       Already-closed years are immutable.</p>

    <div class="flex gap-2 mt-4">
      <button *appCan="'admin.fiscal'" hlmBtn [disabled]="selected() === null" (click)="save()">Save</button>
    </div>
  `,
})
export class FiscalSettingsScreen {
  private readonly service = inject(FiscalService);

  readonly months: MonthOption[] = [
    { value: 1, label: 'January' }, { value: 2, label: 'February' }, { value: 3, label: 'March' },
    { value: 4, label: 'April' }, { value: 5, label: 'May' }, { value: 6, label: 'June' },
    { value: 7, label: 'July' }, { value: 8, label: 'August' }, { value: 9, label: 'September' },
    { value: 10, label: 'October' }, { value: 11, label: 'November' }, { value: 12, label: 'December' },
  ];

  readonly selected = signal<number | null>(null);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);

  constructor() {
    this.service.get().subscribe({
      next: (s) => this.selected.set(s.fiscalYearEndMonth),
      error: () => this.error.set('Could not load fiscal settings.'),
    });
  }

  select(event: Event): void {
    this.selected.set(Number((event.target as HTMLSelectElement).value));
    this.saved.set(false);
  }

  save(): void {
    const month = this.selected();
    if (month === null) return;
    this.error.set(null);
    this.service.set(month).subscribe({
      next: () => this.saved.set(true),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }
}
