import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { FixedAssetsService } from '../../core/fixed-assets/fixed-assets.service';
import { Disposal } from '../../core/fixed-assets/fixed-assets';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-disposal-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/fixed-assets/disposals" class="text-sm text-muted-foreground hover:text-foreground">← Disposals</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (disposal(); as d) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Disposal {{ d.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="d.status === 'Voided'">{{ d.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Asset</td><td class="text-right"><a [routerLink]="['/fixed-assets/assets', d.assetId]" class="text-primary hover:underline">{{ shortId(d.assetId) }}</a></td></tr>
            <tr><td class="py-1 text-muted-foreground">Disposal date</td><td class="text-right">{{ formatDate(d.disposalDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Proceeds</td><td class="text-right tabular-nums">{{ money(d.proceeds) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Catch-up depreciation</td><td class="text-right tabular-nums">{{ money(d.catchUpDepreciation) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Accumulated at disposal</td><td class="text-right tabular-nums">{{ money(d.accumulatedAtDisposal) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Net book value</td><td class="text-right tabular-nums">{{ money(d.netBookValue) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Gain / loss</td><td class="text-right tabular-nums" [class.text-emerald-600]="d.gainLoss > 0" [class.text-destructive]="d.gainLoss < 0">{{ money(d.gainLoss) }}</td></tr>
            @if (d.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ d.memo }}</td></tr> }
          </tbody>
        </table>

        @if (postedEntryId(); as eid) {
          <a [routerLink]="['/journal', eid]" class="text-sm text-primary hover:underline">Posted journal entry →</a>
        }

        @if (d.status === 'Posted') {
          <div *appCan="'fixedassets.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)" [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="voidDisposal()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class DisposalDetail {
  private readonly svc = inject(FixedAssetsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly disposal = signal<Disposal | null>(null);
  readonly postedEntryId = signal<string | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getDisposal(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (d) => { this.disposal.set(d); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
    this.svc.entriesForSource(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (entries) => this.postedEntryId.set(entries[0]?.id ?? null),
      error: () => this.postedEntryId.set(null),
    });
  }

  voidDisposal(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidDisposal(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  shortId(id: string): string { return id.slice(0, 8); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
