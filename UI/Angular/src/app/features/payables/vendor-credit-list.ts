import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { PayablesService } from '../../core/payables/payables.service';
import { VendorCreditApplication } from '../../core/payables/payables';
import { money, displayDate } from '../../core/format/display';
import { extractProblem } from '../../core/api/problem-details';
import { VendorSelect } from '../../shared/vendor-select';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-vendor-credit-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, HlmButton, ...HlmTableImports, VendorSelect, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3 flex-wrap">
        <h1 class="text-2xl font-bold">Credits</h1>
        <app-vendor-select />
        @if (vendorId()) {
          <span class="text-sm text-muted-foreground">Available credit: <span class="tabular-nums font-semibold text-foreground">{{ fmtMoney(balance()) }}</span></span>
        }
        <a *appCan="'ap.write'" hlmBtn size="sm" class="ms-auto"
           routerLink="/payables/credits/new"
           [queryParams]="{ vendor: vendorId() }"
           [class.pointer-events-none]="!vendorId() || balance() <= 0"
           [class.opacity-50]="!vendorId() || balance() <= 0">
          Apply credit
        </a>
      </div>

      @if (svc.vendors().length === 0) {
        <p class="text-muted-foreground text-sm">No vendors yet — <a routerLink="/payables/vendors" class="underline">add one first</a>.</p>
      } @else if (!vendorId()) {
        <p class="text-muted-foreground text-sm">Select a vendor to view credits.</p>
      } @else {
        @if (listError()) { <p class="text-destructive text-sm">{{ listError() }}</p> }
        @if (applications().length === 0 && !listError()) {
          <p class="text-muted-foreground text-sm">No credit applications recorded.</p>
        } @else {
          <div hlmTableContainer>
            <table hlmTable>
              <thead hlmTHead>
                <tr hlmTr><th hlmTh>Date</th><th hlmTh>Applied</th><th hlmTh>Bills</th><th hlmTh>Status</th></tr>
              </thead>
              <tbody hlmTBody>
                @for (c of applications(); track c.id) {
                  <tr hlmTr role="button" tabindex="0"
                      class="cursor-pointer hover:bg-muted/50"
                      [class.opacity-50]="c.voided"
                      (click)="open(c.id)"
                      (keydown.enter)="open(c.id)">
                    <td hlmTd>{{ fmtDate(c.date) }}</td>
                    <td hlmTd class="tabular-nums">{{ fmtMoney(applied(c)) }}</td>
                    <td hlmTd class="tabular-nums">{{ c.allocations.length }}</td>
                    <td hlmTd>{{ c.voided ? 'Voided' : 'Active' }}</td>
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
export class VendorCreditList {
  readonly svc = inject(PayablesService);
  readonly vendorId = this.svc.selectedVendorId;
  private readonly router = inject(Router);
  readonly listError = signal<string | null>(null);

  readonly balance = toSignal(
    toObservable(this.vendorId).pipe(
      switchMap(vid => vid ? this.svc.vendorCreditBalance(vid).pipe(catchError(() => of(0))) : of(0)),
    ),
    { initialValue: 0 },
  );

  readonly applications = toSignal(
    toObservable(this.vendorId).pipe(
      switchMap(vid => {
        if (!vid) return of([] as VendorCreditApplication[]);
        this.listError.set(null);
        return this.svc.listVendorCreditApplications(vid).pipe(
          catchError(e => { this.listError.set(extractProblem(e).detail); return of([] as VendorCreditApplication[]); }),
        );
      }),
    ),
    { initialValue: [] as VendorCreditApplication[] },
  );

  constructor() { this.svc.load(); }

  applied(c: VendorCreditApplication): number { return c.allocations.reduce((s, a) => s + a.amount, 0); }
  open(id: string): void { void this.router.navigate(['/payables/credits', id]); }
  fmtMoney(n: number): string { return money(n); }
  fmtDate(d: string): string { return displayDate(d); }
}
