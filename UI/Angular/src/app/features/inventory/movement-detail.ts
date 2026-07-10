import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { InventoryService } from '../../core/inventory/inventory.service';
import { StockMovement } from '../../core/inventory/inventory';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-movement-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <a routerLink="/inventory" class="text-sm text-muted-foreground hover:text-foreground">← Items</a>
      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (movement(); as m) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">Movement {{ m.number ?? '—' }}</h1>
          <span class="text-sm px-2 py-0.5 rounded" [class.text-destructive]="m.status === 'Void'">{{ m.status }}</span>
        </div>

        <table class="text-sm w-full max-w-md">
          <tbody>
            <tr><td class="py-1 text-muted-foreground">Item</td><td class="text-right"><a [routerLink]="['/inventory/items', m.itemId]" class="text-primary hover:underline">{{ shortId(m.itemId) }}</a></td></tr>
            <tr><td class="py-1 text-muted-foreground">Type</td><td class="text-right">{{ m.type }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Effective date</td><td class="text-right">{{ formatDate(m.effectiveDate) }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Quantity</td><td class="text-right tabular-nums">{{ m.quantity }}</td></tr>
            <tr><td class="py-1 text-muted-foreground">Applied unit cost</td><td class="text-right tabular-nums">{{ money(m.appliedUnitCost) }}</td></tr>
            <tr class="font-semibold border-t border-border"><td class="py-1">Extended cost</td><td class="text-right tabular-nums">{{ money(m.extendedCost) }}</td></tr>
            @if (m.memo) { <tr><td class="py-1 text-muted-foreground">Memo</td><td class="text-right">{{ m.memo }}</td></tr> }
          </tbody>
        </table>

        @if (m.status === 'Posted') {
          <div *appCan="'inventory.write'" class="flex items-center gap-2 border-t border-border pt-4">
            <input hlmInput type="text" placeholder="Void reason (optional)" [value]="reason() ?? ''" (input)="reason.set($any($event.target).value || null)" class="w-64" />
            <button hlmBtn type="button" variant="outline" (click)="voidMovement()" [disabled]="busy()">Void</button>
          </div>
        }
      }
    </div>
  `,
})
export class MovementDetail {
  private readonly svc = inject(InventoryService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly movement = signal<StockMovement | null>(null);
  readonly reason = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  constructor() { this.reload(); }

  reload(): void {
    this.svc.getMovement(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (m) => { this.movement.set(m); this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  voidMovement(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidMovement(this.id, this.reason()).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => this.reload(),
      // The engine is the source of truth for LIFO-safe voidability; a 409 here means a later
      // movement on this item depends on this one's cost — surface it rather than hiding the button.
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  shortId(id: string): string { return id.slice(0, 8); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }
}
