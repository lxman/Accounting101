import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { PayablesService } from '../../core/payables/payables.service';
import { BillView, billTotal } from '../../core/payables/payables';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { SettlementBadge } from '../../shared/settlement-badge';

@Component({
  selector: 'app-bill-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, SettlementBadge, ...HlmTableImports, HlmButton, ...HlmInputImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/payables" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Bills</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ v.bill.number ?? 'Draft' }}</h1>
          <span class="text-xs text-muted-foreground">{{ v.bill.status }}</span>
          <app-settlement-badge [status]="v.settlementStatus" />
        </div>
        <div class="text-sm text-muted-foreground">
          {{ svc.vendorName(v.bill.vendorId) }} · Bill date {{ formatDate(v.bill.billDate) }}
          @if (v.bill.dueDate) { · Due {{ formatDate(v.bill.dueDate) }} }
          @if (v.bill.vendorReference) { · Ref {{ v.bill.vendorReference }} }
        </div>

        <div hlmTableContainer><table hlmTable>
          <thead hlmTHead><tr hlmTr>
            <th hlmTh>Description</th><th hlmTh>Account</th><th hlmTh class="text-right">Amount</th>
          </tr></thead>
          <tbody hlmTBody>
            @for (l of v.bill.lines; track $index) {
              <tr hlmTr>
                <td hlmTd>{{ l.description }}</td>
                <td hlmTd>{{ accountName(l.expenseAccountId) }}</td>
                <td hlmTd class="text-right tabular-nums">{{ money(l.amount) }}</td>
              </tr>
            }
          </tbody>
          <tfoot>
            <tr hlmTr class="font-semibold border-double border-t-4 border-border">
              <td hlmTd colspan="2" class="text-right">Total</td>
              <td hlmTd class="text-right tabular-nums">{{ money(total()) }}</td>
            </tr>
            <tr hlmTr>
              <td hlmTd colspan="2" class="text-right text-muted-foreground">Open balance</td>
              <td hlmTd class="text-right tabular-nums">{{ money(v.openBalance) }}</td>
            </tr>
          </tfoot>
        </table></div>

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

        @switch (v.bill.status) {
          @case ('Draft') {
            <div class="flex items-center gap-2">
              <button hlmBtn type="button" (click)="enter()" [disabled]="busy()">Enter</button>
            </div>
          }
          @case ('Entered') {
            <div class="flex items-center gap-2">
              <input hlmInput type="text" aria-label="Void reason" placeholder="Void reason"
                     [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
              <button hlmBtn type="button" variant="outline" (click)="voidBill()" [disabled]="busy()">Void</button>
            </div>
          }
        }
      } @else {
        <p class="text-muted-foreground text-sm">Loading…</p>
      }
    </div>
  `,
})
export class BillDetail {
  readonly svc = inject(PayablesService);
  readonly accountsSvc = inject(AccountsService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<BillView | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly voidReason = signal('');

  readonly total = computed(() => this.view() ? billTotal(this.view()!.bill.lines) : 0);

  constructor() {
    this.svc.load();
    this.accountsSvc.load();
    this.reload();
  }

  reload(clearBusy = false): void {
    this.svc.getBill(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.view.set(v); if (clearBusy) this.busy.set(false); },
      error: (e) => { this.message.set(extractProblem(e).detail); if (clearBusy) this.busy.set(false); },
    });
  }

  accountName(id: string): string {
    const a = this.accountsSvc.accounts().find(x => x.id === id);
    return a ? `${a.number} · ${a.name}` : id;
  }

  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }

  enter(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.enter(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  voidBill(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.void(this.id, this.voidReason() || null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
