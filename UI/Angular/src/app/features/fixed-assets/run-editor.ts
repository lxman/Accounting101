import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-fa-run-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Run depreciation</h1>
      <p class="text-sm text-muted-foreground">Depreciation for the chosen month is computed for every eligible asset and posted as one journal entry (pending approval).</p>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>Year</label>
          <input hlmInput type="number" [value]="year()" (input)="year.set(+$any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Month</label>
          <select hlmInput [value]="month()" (change)="month.set(+$any($event.target).value)">
            @for (m of months; track m) { <option [value]="m">{{ m }}</option> }
          </select>
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Effective date (optional)</label>
          <input hlmInput type="date" [value]="effectiveDate() ?? ''" (change)="effectiveDate.set($any($event.target).value || null)" />
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Memo</label>
          <input hlmInput type="text" [value]="memo() ?? ''" (input)="memo.set($any($event.target).value || null)" />
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'fixedassets.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Run depreciation</button>
        <a hlmBtn variant="outline" routerLink="/fixed-assets/depreciation-runs">Cancel</a>
      </div>
    </div>
  `,
})
export class RunEditor {
  private readonly svc = inject(FixedAssetsService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly months = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
  readonly year = signal(new Date().getFullYear());
  readonly month = signal(new Date().getMonth() + 1);
  readonly effectiveDate = signal<string | null>(null);
  readonly memo = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly canSave = computed(() => this.year() > 0 && this.month() >= 1 && this.month() <= 12);

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    this.svc.runDepreciation({ year: this.year(), month: this.month(), effectiveDate: this.effectiveDate(), memo: this.memo() })
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (run) => { this.busy.set(false); void this.router.navigate(['/fixed-assets/depreciation-runs', run.id]); },
        error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
      });
  }
}
