import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmButton } from '@spartan-ng/helm/button';
import { ReceivablesService } from '../../core/receivables/receivables.service';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';
import { TruncateDirective } from '../../shared/truncate.directive';

@Component({
  selector: 'app-customer-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmInputImports, HlmButton, CanDirective, TruncateDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">Customers</h1>
      <div class="flex items-end gap-2">
        <div class="flex flex-col gap-1 flex-1"><label class="text-xs text-muted-foreground">Name</label>
          <input hlmInput [value]="newName()" (input)="newName.set($any($event.target).value)" /></div>
        <div class="flex flex-col gap-1 flex-1"><label class="text-xs text-muted-foreground">Email (optional)</label>
          <input hlmInput [value]="newEmail()" (input)="newEmail.set($any($event.target).value)" /></div>
        <button *appCan="'ar.write'" hlmBtn type="button" (click)="add()" [disabled]="!newName().trim() || busy()">Add</button>
      </div>
      @if (svc.loadError()) { <p class="text-destructive text-sm">{{ svc.loadError() }}</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (svc.customers().length === 0 && !svc.loadError()) { <p class="text-sm text-muted-foreground italic">No customers yet.</p> }
      @for (c of svc.customers(); track c.id) {
        <div data-testid="customer-row"
             class="flex items-center gap-3 py-1 border-b border-border/50 text-sm cursor-pointer hover:bg-muted/50"
             role="button" tabindex="0"
             (click)="open(c.id)" (keydown.enter)="open(c.id)">
          <span appTruncate>{{ c.name }}</span><span class="text-muted-foreground" appTruncate>{{ c.email }}</span>
        </div>
      }
    </div>`,
})
export class CustomerList {
  readonly svc = inject(ReceivablesService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router);
  readonly newName = signal('');
  readonly newEmail = signal('');
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  constructor() { this.svc.load(); }

  add(): void {
    const name = this.newName().trim();
    if (!name) return;
    this.busy.set(true);
    this.error.set(null);
    this.svc.create(name, this.newEmail().trim() || null).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.newName.set(''); this.newEmail.set(''); this.busy.set(false); },
      error: (e) => { this.error.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  open(id: string): void { void this.router.navigate(['/receivables/customers', id]); }
}
