import { ChangeDetectionStrategy, Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { InvoiceView, invoiceTotals, lineAmount, Payment } from '../../core/receivables/receivables';
import { extractProblem } from '../../core/api/problem-details';
import { money as fmtMoney, displayDate as fmtDate } from '../../core/format/display';
import { InvoiceStatusBadge } from '../../shared/invoice-status-badge';
import { SettlementBadge } from '../../shared/settlement-badge';

@Component({
  selector: 'app-invoice-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, InvoiceStatusBadge, SettlementBadge, ...HlmTableImports, HlmButton, ...HlmInputImports],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <a routerLink="/receivables" class="text-sm text-muted-foreground hover:text-foreground w-fit">← Invoices</a>
      @if (view(); as v) {
        <div class="flex items-center gap-3">
          <h1 class="text-2xl font-bold">{{ v.invoice.number ?? 'Draft' }}</h1>
          <app-invoice-status-badge [status]="v.invoice.status" />
          <app-settlement-badge [status]="v.settlementStatus" />
        </div>
        <div class="text-sm text-muted-foreground">
          {{ svc.customerName(v.invoice.customerId) }} · Issued {{ formatDate(v.invoice.issueDate) }}
          @if (v.invoice.dueDate) { · Due {{ formatDate(v.invoice.dueDate) }} }
        </div>

        <div hlmTableContainer><table hlmTable>
          <thead hlmTHead>
            <tr hlmTr>
              <th hlmTh>Description</th>
              <th hlmTh class="text-right">Qty</th>
              <th hlmTh class="text-right">Unit</th>
              <th hlmTh class="text-right">Amount</th>
            </tr>
          </thead>
          <tbody hlmTBody>
            @for (l of v.invoice.lines; track $index) {
              <tr hlmTr>
                <td hlmTd>{{ l.description }}</td>
                <td hlmTd class="text-right tabular-nums">{{ l.quantity }}</td>
                <td hlmTd class="text-right tabular-nums">{{ money(l.unitPrice) }}</td>
                <td hlmTd class="text-right tabular-nums">{{ money(lineAmt(l)) }}</td>
              </tr>
            }
          </tbody>
          @if (totals(); as t) {
            <tfoot>
              <tr hlmTr>
                <td hlmTd colspan="3" class="text-right text-muted-foreground">Subtotal</td>
                <td hlmTd class="text-right tabular-nums">{{ money(t.subtotal) }}</td>
              </tr>
              <tr hlmTr>
                <td hlmTd colspan="3" class="text-right text-muted-foreground">Tax</td>
                <td hlmTd class="text-right tabular-nums">{{ money(t.tax) }}</td>
              </tr>
              <tr hlmTr class="font-semibold border-double border-t-4 border-border">
                <td hlmTd colspan="3" class="text-right">Total</td>
                <td hlmTd class="text-right tabular-nums">{{ money(t.total) }}</td>
              </tr>
              <tr hlmTr>
                <td hlmTd colspan="3" class="text-right text-muted-foreground">Open balance</td>
                <td hlmTd class="text-right tabular-nums">{{ money(v.openBalance) }}</td>
              </tr>
            </tfoot>
          }
        </table></div>

        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

        @switch (v.invoice.status) {
          @case ('Draft') {
            <div class="flex items-center gap-2">
              <a hlmBtn variant="outline" [routerLink]="['/receivables/invoices', id, 'edit']">Edit</a>
              <button hlmBtn type="button" variant="outline" (click)="deleteInvoice()" [disabled]="busy()">Delete</button>
              <button hlmBtn type="button" (click)="issue()" [disabled]="busy()">Issue</button>
            </div>
          }
          @case ('Issued') {
            <div class="flex items-center gap-2">
              <input hlmInput type="text" aria-label="Void reason" placeholder="Void reason"
                     [value]="voidReason()" (input)="voidReason.set($any($event.target).value)" />
              <button hlmBtn type="button" variant="outline" (click)="voidInvoice()" [disabled]="busy()">Void</button>
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
                            <button hlmBtn type="button" variant="ghost" size="sm"
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
export class InvoiceDetail {
  readonly svc = inject(ReceivablesService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  // Not readonly: issuing a draft promotes it to a new evidentiary id, after which the page re-points here.
  id = this.route.snapshot.paramMap.get('id')!;
  readonly view = signal<InvoiceView | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly voidReason = signal('');
  readonly payments = signal<Payment[]>([]);
  readonly applied = computed(() => this.payments()
    .map(p => ({ payment: p, here: p.allocations.filter(a => a.targetId === this.id).reduce((s, a) => s + a.amount, 0) }))
    .filter(x => x.here > 0));

  readonly totals = computed(() => this.view() ? invoiceTotals(this.view()!.invoice.lines, this.view()!.invoice.taxRate) : null);

  constructor() {
    this.svc.load();
    this.reload();
  }

  reload(clearBusy = false): void {
    this.svc.getInvoice(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => {
        this.view.set(v);
        if (v.invoice.status === 'Issued') this.loadPayments(v.invoice.customerId);
        if (clearBusy) this.busy.set(false);
      },
      error: (e) => { this.message.set(extractProblem(e).detail); if (clearBusy) this.busy.set(false); },
    });
  }

  private loadPayments(customerId: string): void {
    this.svc.listPayments(customerId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (ps) => this.payments.set(ps),
      error: () => this.payments.set([]),
    });
  }

  voidPayment(p: Payment): void {
    this.busy.set(true); this.message.set(null);
    this.svc.voidPayment(p.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  lineAmt(l: { quantity: number; unitPrice: number }): number { return lineAmount(l); }
  money(n: number): string { return fmtMoney(n); }
  formatDate(d: string): string { return fmtDate(d); }

  issue(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.issue(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      // Issuing promotes the draft to a brand-new evidentiary invoice (new id + number) and deletes the
      // draft. Re-point the page at the issued id; reloading the old draft id would 404 (it's gone).
      next: (issued) => {
        this.id = issued.id;
        this.router.navigate(['/receivables/invoices', issued.id], { replaceUrl: true });
        this.reload(true);
      },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  voidInvoice(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.void(this.id, this.voidReason() || null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.reload(true); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  deleteInvoice(): void {
    this.busy.set(true); this.message.set(null);
    this.svc.deleteDraft(this.id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); this.router.navigate(['/receivables']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
