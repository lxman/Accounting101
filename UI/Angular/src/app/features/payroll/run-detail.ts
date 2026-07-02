import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { PayrollService } from '../../core/payroll/payroll.service';
import { PayrollRun, netPay } from '../../core/payroll/payroll';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-run-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/payroll/runs" class="text-sm text-muted-foreground hover:text-foreground">← Runs</a>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (run(); as r) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Payroll run {{ r.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="r.status === 'Void'">{{ r.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Pay date</td><td class="text-right">{{ formatDate(r.payDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Gross</td><td class="text-right tabular-nums">{{ money(r.gross) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Employee FICA</td><td class="text-right tabular-nums">{{ money(r.employeeFica) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Employer FICA</td><td class="text-right tabular-nums">{{ money(r.employerFica) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Income tax withheld</td><td class="text-right tabular-nums">{{ money(r.incomeTaxWithheld) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Deductions</td><td class="text-right tabular-nums">{{ money(r.deductions) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Net pay</td><td class="text-right tabular-nums">{{ money(net(r)) }}</td></tr>
            @if (r.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ r.memo }}</td></tr> }
          </tbody>
        </table>

        @if (postedEntryId(); as eid) {
          <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a>
        }

        @if (r.status === 'Posted') {
          <div *appCan="'payroll.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)"
                   [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="this.void()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class RunDetail {
  private readonly svc = inject(PayrollService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly run = signal<PayrollRun | null>(null);
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getRun(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.run.set(r); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  void(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidRun(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  net(r: PayrollRun): number { return netPay(r); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
