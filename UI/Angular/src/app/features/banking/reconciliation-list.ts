import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatement } from '../../core/banking/banking';
import { PagedResponse } from '../../core/api/paged-response';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-reconciliation-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective, ...HlmSelectImports, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <h1 class="text-2xl font-bold">Reconcile</h1>
      <p class="text-sm text-muted-foreground">Pick a cash account, then start a reconciliation from one of its posted statements.</p>

      <div class="flex items-center gap-2 max-w-md">
        <label class="text-sm text-muted-foreground">Cash account</label>
        <div hlmSelect [value]="cashAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="cashAccountId.set($any($event))" class="w-full">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a cash account" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (a of cashAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      @if (!cashAccountId()) {
        <p class="text-muted-foreground text-sm">Select a cash account to see its statements.</p>
      } @else if (statements().length === 0) {
        <p class="text-muted-foreground text-sm">No posted statements for this account.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead><tr hlmTr><th hlmTh>Statement</th><th hlmTh>Date</th>
              <th hlmTh class="text-right">Closing</th><th hlmTh></th></tr></thead>
            <tbody hlmTBody>
              @for (s of statements(); track s.id) {
                <tr hlmTr>
                  <td hlmTd>{{ s.number ?? '—' }}</td>
                  <td hlmTd>{{ date(s.statementDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(s.closingBalance) }}</td>
                  <td hlmTd class="text-right">
                    <button *appCan="'bankrec.write'" hlmBtn size="sm" (click)="start(s.id)" [disabled]="busy()">Reconcile</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class ReconciliationList {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly client = inject(ClientContextService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly cashAccountId = signal<string | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));

  constructor() {
    if (this.accounts.accounts().length === 0) this.accounts.load();
    const preselected = this.route.snapshot.queryParamMap.get('statement');
    if (preselected) this.start(preselected);
  }

  private readonly query = computed(() => ({ id: this.client.clientId(), account: this.cashAccountId() }));
  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.message.set(null)),
      switchMap(({ id, account }) => {
        if (!id || !account) return of(null);
        return this.svc.listStatements(account, { skip: 0, limit: 50 }).pipe(catchError(() => of(null)));
      }),
    ),
    { initialValue: null as PagedResponse<BankStatement> | null },
  );

  readonly statements = computed(() => (this.page()?.items ?? []).filter(s => s.status === 'Posted'));
  readonly accountItemToString = (id: string): string => this.accounts.label(id);

  start(statementId: string): void {
    this.busy.set(true); this.message.set(null);
    this.svc.startReconciliation(statementId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.busy.set(false); void this.router.navigate(['/cash/reconciliation', r.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
