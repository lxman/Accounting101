import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { TaxRemittance, remittanceTotal } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-remittance-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/payroll/remittances" class="text-sm text-muted-foreground hover:text-foreground">← Remittances</a>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (remittance(); as m) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Tax remittance {{ m.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="m.status === 'Void'">{{ m.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Pay date</td><td class="text-right">{{ formatDate(m.payDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Withholdings</td><td class="text-right tabular-nums">{{ money(m.withholdingsAmount) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Taxes</td><td class="text-right tabular-nums">{{ money(m.taxesAmount) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Total</td><td class="text-right tabular-nums">{{ money(total(m)) }}</td></tr>
            @if (m.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ m.memo }}</td></tr> }
          </tbody>
        </table>

        @if (postedEntryId(); as eid) {
          <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a>
        }

        @if (m.status === 'Posted') {
          <div class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)"
                   [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button *appCan="'payroll.write'" hlmBtn type="button" variant="outline" (click)="this.void()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class RemittanceDetail {
  private readonly svc = inject(PayrollService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly remittance = signal<TaxRemittance | null>(null);
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getRemittance(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (m) => { this.remittance.set(m); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  void(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidRemittance(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  total(m: TaxRemittance): number { return remittanceTotal(m); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
