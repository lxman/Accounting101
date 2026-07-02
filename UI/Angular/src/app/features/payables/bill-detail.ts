import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { PayablesService } from '../../core/payables/payables.service';
import { BillView, BillPayment, billTotal } from '../../core/payables/payables';
import { AccountsService } from '../../core/accounts/accounts.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { SettlementBadge } from '../../shared/settlement-badge';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-bill-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, SettlementBadge, CanDirective, ...HlmTableImports, HlmButton, ...HlmInputImports],
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
              <a *appCan="'ap.write'" hlmBtn variant="outline" [routerLink]="['/payables/bills', id, 'edit']">Edit</a>
              <button *appCan="'ap.write'" hlmBtn type="button" variant="outline" (click)="deleteBill()" [disabled]="busy()">Delete</button>
              <button *appCan="'ap.write'" hlmBtn type="button" (click)="enter()" [disabled]="busy()">Enter</button>
            </div>
          }
          @case ('Entered') {
            <div class="flex items-center gap-2">
              <input hlmInput type="text" aria-label="Void reason" placeholder="Void reason"
                     [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
              <button *appCan="'ap.write'" hlmBtn type="button" variant="outline" (click)="voidBill()" [disabled]="busy()">Void</button>
            </div>
            @if (applied().length > 0) {
              <div class="flex flex-col gap-1">
                <h2 class="text-sm font-semibold text-muted-foreground">Applied payments</h2>
                <table class="text-sm w-full max-w-md">
                  <tbody>
                    @for (a of applied(); track a.payment.id) {
                      <tr [class.opacity-50]="a.payment.voided">
                        <td class="py-1">{{ formatDate(a.payment.date) }}</td>
                        <td class="tabular-nums">{{ money(a.here) }}</td>
                        <td class="text-muted-foreground">{{ a.payment.method ?? '—' }}</td>
                        <td class="text-right">
                          @if (!a.payment.voided) {
                            <button *appCan="'ap.write'" hlmBtn type="button" variant="ghost" size="sm"
                                    (click)="voidPayment(a.payment)" [disabled]="busy()">Void</button>
                          } @else {
                            <span class="text-xs text-muted-foreground">Voided</span>
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
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
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  // Not readonly: entering a draft promotes it to a new evidentiary id, after which the page re-points here.
  id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<BillView | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly voidReason = signal('');
  readonly payments = signal<BillPayment[]>([]);
  readonly applied = computed(() => this.payments()
    .map(p => ({ payment: p, here: p.allocations.filter(a => a.targetId === this.id).reduce((s, a) => s + a.amount, 0) }))
    .filter(x => x.here > 0));

  readonly total = computed(() => this.view() ? billTotal(this.view()!.bill.lines) : 0);

  constructor() {
    this.svc.load();
    this.accountsSvc.load();
    this.reload();
  }

  reload(clearBusy = false): void {
    this.svc.getBill(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => {
        this.view.set(v);
        if (v.bill.status === 'Entered') this.loadPayments(v.bill.vendorId);
        if (clearBusy) this.busy.set(false);
      },
      error: (e) => { this.message.set(extractProblem(e).detail); if (clearBusy) this.busy.set(false); },
    });
  }

  private loadPayments(vendorId: string): void {
    this.svc.listBillPayments(vendorId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (ps) => this.payments.set(ps),
      error: () => this.payments.set([]),
    });
  }

  voidPayment(p: BillPayment): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidBillPayment(p.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
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
      // Entering promotes the draft to a brand-new evidentiary bill (new id + number) and deletes the draft.
      // Re-point the page at the entered id; reloading the old draft id would 404 (it's gone).
      next: (entered) => {
        this.id = entered.id;
        this.router.navigate(['/payables/bills', entered.id], { replaceUrl: true });
        this.reload(true);
      },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  deleteBill(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.discardBill(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.router.navigate(['/payables']); },
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
