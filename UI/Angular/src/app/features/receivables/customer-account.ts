import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { CustomerAccountView } from '../../core/receivables/receivables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-customer-account',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <a routerLink="/receivables/customers" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Customers</a>
      @if (error()) {
        <p class="text-destructive text-sm">{{ error() }}</p>
      } @else if (account(); as a) {
        <div class="flex flex-wrap items-baseline gap-x-6 gap-y-1">
          <h1 class="text-2xl font-bold">{{ a.customer.name }}</h1>
          <span class="text-sm text-muted-foreground">{{ a.customer.email ?? '—' }}</span>
          <span class="ms-auto text-sm">AR balance <span class="font-semibold tabular-nums">{{ money(a.arBalance) }}</span></span>
          <span class="text-sm">Credit <span class="font-semibold tabular-nums">{{ money(a.creditBalance) }}</span></span>
        </div>

        <div class="flex flex-wrap gap-4 text-sm">
          <div>Current <span class="tabular-nums">{{ money(a.aging.current) }}</span></div>
          <div>1–30 <span class="tabular-nums">{{ money(a.aging.d1To30) }}</span></div>
          <div>31–60 <span class="tabular-nums">{{ money(a.aging.d31To60) }}</span></div>
          <div>61–90 <span class="tabular-nums">{{ money(a.aging.d61To90) }}</span></div>
          <div [class.text-destructive]="a.aging.d90Plus > 0">90+ <span class="tabular-nums">{{ money(a.aging.d90Plus) }}</span></div>
        </div>

        <div class="grid grid-cols-1 lg:grid-cols-3 gap-6">
          <div class="lg:col-span-2 flex flex-col gap-4">
            <section class="flex flex-col gap-1">
              <h2 class="font-semibold text-sm">Open invoices</h2>
              @if (a.openInvoices.length === 0) { <p class="text-sm text-muted-foreground">No open invoices.</p> }
              @else {
                <table class="w-full text-sm">
                  <thead><tr class="text-left text-muted-foreground"><th class="py-1">Number</th><th>Issued</th><th>Due</th><th class="text-right">Open</th><th class="text-right">Overdue</th></tr></thead>
                  <tbody>
                    @for (l of a.openInvoices; track l.invoiceId) {
                      <tr [class.text-destructive]="l.daysOverdue > 0">
                        <td class="py-1">{{ l.number ?? '—' }}</td><td>{{ fmtDate(l.issueDate) }}</td>
                        <td>{{ l.dueDate ? fmtDate(l.dueDate) : '—' }}</td>
                        <td class="text-right tabular-nums">{{ money(l.openBalance) }}</td>
                        <td class="text-right tabular-nums">{{ l.daysOverdue > 0 ? l.daysOverdue + 'd' : '—' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </section>

            <section class="flex flex-col gap-1">
              <h2 class="font-semibold text-sm">Statement of account</h2>
              @if (a.statementLines.length === 0) { <p class="text-sm text-muted-foreground">No statement activity.</p> }
              @else {
                <table class="w-full text-sm">
                  <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Type</th><th>Ref</th><th class="text-right">Charge</th><th class="text-right">Payment</th><th class="text-right">Balance</th></tr></thead>
                  <tbody>
                    @for (s of a.statementLines; track $index) {
                      <tr>
                        <td class="py-1">{{ fmtDate(s.date) }}</td><td>{{ s.type }}</td><td>{{ s.reference ?? '—' }}</td>
                        <td class="text-right tabular-nums">{{ s.charge ? money(s.charge) : '' }}</td>
                        <td class="text-right tabular-nums">{{ s.payment ? money(s.payment) : '' }}</td>
                        <td class="text-right tabular-nums font-medium">{{ money(s.balance) }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            </section>
          </div>

          <section class="flex flex-col gap-1">
            <h2 class="font-semibold text-sm">Credit activity</h2>
            @if (a.creditLines.length === 0) { <p class="text-sm text-muted-foreground">No credit activity.</p> }
            @else {
              <table class="w-full text-sm">
                <thead><tr class="text-left text-muted-foreground"><th class="py-1">Date</th><th>Type</th><th class="text-right">Amount</th><th class="text-right">Balance</th></tr></thead>
                <tbody>
                  @for (c of a.creditLines; track $index) {
                    <tr>
                      <td class="py-1">{{ fmtDate(c.date) }}</td><td>{{ c.type }}</td>
                      <td class="text-right tabular-nums" [class.text-destructive]="c.amount < 0">{{ money(c.amount) }}</td>
                      <td class="text-right tabular-nums font-medium">{{ money(c.creditBalance) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </section>
        </div>
      } @else {
        <p class="text-sm text-muted-foreground">Loading…</p>
      }
    </div>
  `,
})
export class CustomerAccount {
  private readonly svc = inject(ReceivablesService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly account = signal<CustomerAccountView | null>(null);
  readonly error = signal<string | null>(null);

  constructor() {
    const id = this.route.snapshot.paramMap.get('id');
    if (!id) { this.error.set('No customer.'); return; }
    this.svc.getCustomerAccount(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: v => this.account.set(v),
      error: e => this.error.set(extractProblem(e).detail),
    });
  }

  money(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
