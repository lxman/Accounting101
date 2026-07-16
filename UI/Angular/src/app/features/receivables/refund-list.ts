import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal, takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BehaviorSubject, catchError, combineLatest, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { Refund } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CustomerSelect } from '../../shared/customer-select';
import { CanDirective } from '../../core/capabilities/can.directive';
import { TruncateDirective } from '../../shared/truncate.directive';

@Component({
  selector: 'app-refund-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect, CanDirective, TruncateDirective],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Refunds</h1>
        <app-customer-select />
        <a *appCan="'ar.write'" hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/refunds/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          Issue refund
        </a>
      </div>

      @if (svc.customers().length === 0) {
        <p class="text-muted-foreground text-sm">No customers yet — <a routerLink="/receivables/customers" class="underline">add one first</a>.</p>
      } @else if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view refunds.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (refunds().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No refunds recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Date</th><th hlmTh>Amount</th><th hlmTh>Memo</th><th hlmTh>Status</th><th hlmTh></th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (r of refunds(); track r.id) {
                  <tr hlmTr role="button" tabindex="0"
                      class="cursor-pointer hover:bg-muted/50"
                      [class.opacity-50]="r.voided"
                      (click)="open(r.id)"
                      (keydown.enter)="open(r.id)">
                    <td hlmTd>{{ fmtDate(r.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(r.amount) }}</td>
                    <td hlmTd><span appTruncate>{{ r.memo ?? '—' }}</span></td>
                    <td hlmTd>{{ r.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (!r.voided) {
                        <button *appCan="'ar.write'" hlmBtn size="sm" variant="outline"
                                (click)="$event.stopPropagation(); doVoid(r)"
                                (keydown.enter)="$event.stopPropagation()"
                                [disabled]="busy()">Void</button>
                      } @else { <span class="text-muted-foreground">—</span> }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    </div>
  `,
})
export class RefundList {
  readonly svc = inject(ReceivablesService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  readonly customerId = this.svc.selectedCustomerId;
  readonly listError = signal<string | null>(null);
  readonly busy = signal(false);
  private readonly refresh$ = new BehaviorSubject(0);

  readonly refunds = toSignal(
    combineLatest([toObservable(this.customerId), this.refresh$]).pipe(
      switchMap(([cid]) => {
        if (!cid) return of([] as Refund[]);
        this.listError.set(null);
        return this.svc.listRefunds(cid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as Refund[]); }),
        );
      }),
    ),
    { initialValue: [] as Refund[] },
  );

  constructor() { this.svc.load(); }

  doVoid(r: Refund): void {
    this.busy.set(true); this.listError.set(null);
    this.svc.voidRefund(r.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.refresh$.next(this.refresh$.value + 1); },
      error: e => { this.listError.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  open(id: string): void { void this.router.navigate(['/receivables/refunds', id]); }

  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
