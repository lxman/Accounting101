import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { BankingService } from '../../core/banking/banking.service';
import { BankStatement } from '../../core/banking/banking';
import { PagedResponse } from '../../core/api/paged-response';
import { AccountsService } from '../../core/accounts/accounts.service';
import { ClientContextService } from '../../core/client/client-context.service';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-statement-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, CanDirective, ...HlmSelectImports, ...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Bank statements</h1>
        <div class="ms-auto flex gap-2">
          <a *appCan="'bankrec.write'" hlmBtn size="sm" variant="outline" routerLink="/cash/statements/import">Import</a>
          <a *appCan="'bankrec.write'" hlmBtn size="sm" routerLink="/cash/statements/new">New statement</a>
        </div>
      </div>

      <div class="flex items-center gap-2 max-w-md">
        <label class="text-sm text-muted-foreground">Cash account</label>
        <div hlmSelect [value]="cashAccountId() ?? ''" [itemToString]="accountItemToString" (valueChange)="cashAccountId.set($any($event))" class="w-full">
          <hlm-select-trigger class="w-full"><hlm-select-value placeholder="Select a cash account" /></hlm-select-trigger>
          <hlm-select-content *hlmSelectPortal>
            @for (a of cashAccounts(); track a.id) { <hlm-select-item [value]="a.id">{{ a.number }} {{ a.name }}</hlm-select-item> }
          </hlm-select-content>
        </div>
      </div>

      @if (!cashAccountId()) {
        <p class="text-muted-foreground text-sm">Select a cash account to see its statements.</p>
      } @else if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      } @else if (statements().length === 0) {
        <p class="text-muted-foreground text-sm">No statements for this account yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr><th hlmTh>Number</th><th hlmTh>Statement date</th>
                <th hlmTh class="text-right">Opening</th><th hlmTh class="text-right">Closing</th><th hlmTh>Status</th></tr>
            </thead>
            <tbody hlmTBody>
              @for (s of statements(); track s.id) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" tabindex="0" (click)="open(s.id)" (keydown.enter)="open(s.id)">
                  <td hlmTd>{{ s.number ?? '—' }}</td>
                  <td hlmTd>{{ date(s.statementDate) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(s.openingBalance) }}</td>
                  <td hlmTd class="text-right tabular-nums">{{ money(s.closingBalance) }}</td>
                  <td hlmTd [class.text-destructive]="s.status === 'Void'">{{ s.status }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class StatementList {
  private readonly svc = inject(BankingService);
  private readonly accounts = inject(AccountsService);
  private readonly client = inject(ClientContextService);
  private readonly router = inject(Router);

  readonly cashAccountId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly cashAccounts = computed(() => this.accounts.accounts().filter(a => a.type === 'Asset' && a.postable));

  constructor() { if (this.accounts.accounts().length === 0) this.accounts.load(); }

  private readonly query = computed(() => ({ id: this.client.clientId(), account: this.cashAccountId() }));
  private readonly page = toSignal(
    toObservable(this.query).pipe(
      tap(() => this.error.set(null)),
      switchMap(({ id, account }) => {
        if (!id || !account) return of(null);
        return this.svc.listStatements(account, { skip: 0, limit: 50 }).pipe(
          catchError((e: unknown) => { this.error.set((e as { message?: string })?.message ?? 'Error loading statements'); return of(null); }),
        );
      }),
    ),
    { initialValue: null as PagedResponse<BankStatement> | null },
  );

  readonly statements = computed(() => this.page()?.items ?? []);
  readonly accountItemToString = (id: string): string => this.accounts.label(id);
  open(id: string): void { void this.router.navigate(['/cash/statements', id]); }
  money(n: number): string { return fmtMoney(n); }
  date(d: string): string { return fmtDate(d); }
}
