import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { PeriodsService } from '../../core/periods/periods.service';
import { PeriodStatus } from '../../core/periods/periods';
import { displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December'];

@Component({
  selector: 'app-period-close',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Period Close</h1>

      @if (loading()) { <p class="text-muted-foreground text-sm">Loading…</p> }
      @if (loadError()) { <p class="text-destructive text-sm">{{ loadError() }}</p> }

      @if (status(); as s) {
        <div class="rounded-lg border border-border p-4 flex flex-col gap-1">
          @if (s.closedThrough) {
            <p class="text-sm">Closed through <span class="font-semibold">{{ date(s.closedThrough) }}</span></p>
          } @else {
            <p class="text-sm text-muted-foreground">No periods have been closed yet.</p>
          }
          <p class="text-xs text-muted-foreground">Fiscal year ends in {{ monthName(s.fiscalYearEndMonth) }}.</p>
        </div>

        <p class="text-xs text-muted-foreground">
          Closed periods are final. To correct a closed period, post an adjusting entry in the current period.
        </p>

        <div class="rounded-lg border border-border p-4 flex flex-col gap-3">
          <p class="text-sm">Next period to close: <span class="font-semibold">{{ nextLabel() }}</span></p>
          @if (actionError()) { <p class="text-destructive text-sm">{{ actionError() }}</p> }

          <button type="button" *appCan="'gl.close'"
                  class="self-start text-sm px-3 py-1.5 rounded-lg bg-primary text-primary-foreground disabled:opacity-50"
                  [disabled]="busy()" (click)="closeNext()">
            Close {{ nextLabel() }}
          </button>

          <div class="flex items-center gap-2 text-sm" *appCan="'gl.close'">
            <span class="text-muted-foreground">Other period:</span>
            <select class="rounded-md border border-border bg-background px-2 py-1"
                    (change)="pickMonth.set(+$any($event.target).value)">
              @for (m of months; track m.value) { <option [value]="m.value" [selected]="m.value === pickMonth()">{{ m.label }}</option> }
            </select>
            <select class="rounded-md border border-border bg-background px-2 py-1"
                    (change)="pickYear.set(+$any($event.target).value)">
              @for (y of years(); track y) { <option [value]="y" [selected]="y === pickYear()">{{ y }}</option> }
            </select>
            <button type="button" class="text-sm px-3 py-1.5 rounded-lg border border-border disabled:opacity-50"
                    [disabled]="busy()" (click)="closePicked()">Close</button>
          </div>
        </div>
      }
    </div>
  `,
})
export class PeriodClose {
  protected readonly svc = inject(PeriodsService);
  private readonly destroyRef = inject(DestroyRef);

  readonly status = signal<PeriodStatus | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busy = signal(false);

  readonly months = MONTHS.map((label, i) => ({ value: i + 1, label }));
  readonly pickMonth = signal(1);
  readonly pickYear = signal(2026);
  readonly years = computed(() => { const y = this.pickYear(); return [y - 2, y - 1, y, y + 1]; });

  constructor() { this.load(); }

  load(): void {
    this.loading.set(true);
    this.actionError.set(null);
    this.svc.status().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (s) => {
        this.status.set(s);
        const np = this.nextPeriod();
        this.pickMonth.set(np.month);
        this.pickYear.set(np.year);
        this.loading.set(false);
      },
      error: (e) => { this.loadError.set(extractProblem(e).detail); this.loading.set(false); },
    });
  }

  date(d: string): string { return displayDate(d); }
  monthName(m: number): string { return MONTHS[m - 1] ?? String(m); }

  lastDay(year: number, month: number): string {
    const d = new Date(Date.UTC(year, month, 0)); // day 0 of the next month = last day of `month`
    return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
  }

  nextPeriod(): { year: number; month: number } {
    const ct = this.status()?.closedThrough;
    if (ct) {
      const [y, m] = ct.split('-').map(Number);
      return m >= 12 ? { year: y + 1, month: 1 } : { year: y, month: m + 1 };
    }
    const now = new Date();
    return now.getUTCMonth() === 0
      ? { year: now.getUTCFullYear() - 1, month: 12 }
      : { year: now.getUTCFullYear(), month: now.getUTCMonth() }; // 0-based current = 1-based previous
  }

  nextEnd(): string { const np = this.nextPeriod(); return this.lastDay(np.year, np.month); }
  nextLabel(): string { const np = this.nextPeriod(); return `${this.monthName(np.month)} ${np.year}`; }

  closeNext(): void { this.runClose(this.nextEnd()); }
  closePicked(): void { this.runClose(this.lastDay(this.pickYear(), this.pickMonth())); }

  protected runClose(asOf: string): void {
    this.actionError.set(null);
    this.busy.set(true);
    this.svc.close(asOf).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.load(); },
      error: (e) => { this.actionError.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
