import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { BehaviorSubject, catchError, combineLatest, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { CreditDocument, CreditType } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { CustomerSelect } from '../../shared/customer-select';
import { CanDirective } from '../../core/capabilities/can.directive';
import { TruncateDirective } from '../../shared/truncate.directive';

@Component({
  selector: 'app-credit-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, CustomerSelect, CanDirective, TruncateDirective],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Credits</h1>
        <app-customer-select />
        <a *appCan="'ar.write'" hlmBtn size="sm" class="ms-auto"
           routerLink="/receivables/credits/new"
           [queryParams]="{ customer: customerId() }"
           [class.pointer-events-none]="!customerId()"
           [class.opacity-50]="!customerId()">
          Record adjustment
        </a>
      </div>

      @if (svc.customers().length === 0) {
        <p class="text-muted-foreground text-sm">No customers yet — <a routerLink="/receivables/customers" class="underline">add one first</a>.</p>
      } @else if (!customerId()) {
        <p class="text-muted-foreground text-sm">Select a customer to view credits.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (credits().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No credits recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr>
                  <th hlmTh>Date</th><th hlmTh>Type</th><th hlmTh>Amount</th>
                  <th hlmTh>Memo</th><th hlmTh>Status</th><th hlmTh></th>
                </tr>
              </thead>
              <tbody hlmTBody>
                @for (c of credits(); track c.id) {
                  <tr hlmTr role="button" tabindex="0"
                      class="cursor-pointer hover:bg-muted/50"
                      [class.opacity-50]="c.voided"
                      (click)="open(c.type, c.id)"
                      (keydown.enter)="open(c.type, c.id)">
                    <td hlmTd>{{ fmtDate(c.date) }}</td>
                    <td hlmTd>{{ label(c.type) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(c.amount) }}</td>
                    <td hlmTd><span appTruncate>{{ c.memo ?? '—' }}</span></td>
                    <td hlmTd>{{ c.voided ? 'Voided' : 'Active' }}</td>
                    <td hlmTd>
                      @if (c.type !== 'credit-application' && !c.voided) {
                        <button *appCan="'ar.write'" hlmBtn size="sm" variant="outline"
                                (click)="$event.stopPropagation(); doVoid(c)"
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
export class CreditList {
  readonly svc = inject(ReceivablesService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  readonly customerId = this.svc.selectedCustomerId;
  readonly listError = signal<string | null>(null);
  readonly busy = signal(false);
  private readonly refresh$ = new BehaviorSubject(0);

  readonly credits = toSignal(
    combineLatest([toObservable(this.customerId), this.refresh$]).pipe(
      switchMap(([cid]) => {
        if (!cid) return of([] as CreditDocument[]);
        this.listError.set(null);
        return this.svc.listCredits(cid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as CreditDocument[]); }),
        );
      }),
    ),
    { initialValue: [] as CreditDocument[] },
  );

  constructor() { this.svc.load(); }

  doVoid(c: CreditDocument): void {
    if (c.type === 'credit-application') return;
    this.busy.set(true); this.listError.set(null);
    this.svc.voidCredit(c.type, c.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.refresh$.next(this.refresh$.value + 1); },
      error: e => { this.listError.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  open(type: CreditType, id: string): void { void this.router.navigate(['/receivables/credits', type, id]); }

  label(t: CreditType): string {
    return t === 'credit-note' ? 'Credit note' : t === 'write-off' ? 'Write-off' : 'Apply credit';
  }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
