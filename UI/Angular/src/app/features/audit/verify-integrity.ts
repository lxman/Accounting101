import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HlmButton } from '@spartan-ng/helm/button';
import { AuditService } from '../../core/audit/audit.service';
import { AuditVerifyResponse } from '../../core/audit/audit';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-verify-integrity',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-xl">
      <h1 class="text-2xl font-bold">Verify Integrity</h1>
      <p class="text-sm text-muted-foreground">
        Recompute the client's audit hash chain and reconcile it against the guarded chain head.
      </p>
      <button hlmBtn size="sm" class="w-fit" [disabled]="checking()" (click)="check()">Check integrity</button>

      @if (checking()) { <p class="text-muted-foreground text-sm">Checking…</p> }
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      @if (result(); as r) {
        @if (r.valid) {
          <div class="rounded border border-border p-3 text-sm text-green-700 dark:text-green-400">
            ✅ Audit chain intact — {{ r.recordCount }} records verified.
          </div>
        } @else {
          <div class="rounded border border-destructive p-3 text-sm text-destructive flex flex-col gap-1">
            <span>❌ Integrity check failed: {{ describe(r) }}</span>
            <span class="text-muted-foreground">Contact a deployment administrator.</span>
          </div>
        }
      }
    </div>
  `,
})
export class VerifyIntegrity {
  private readonly svc = inject(AuditService);
  private readonly destroyRef = inject(DestroyRef);

  readonly result = signal<AuditVerifyResponse | null>(null);
  readonly checking = signal(false);
  readonly error = signal<string | null>(null);

  check(): void {
    this.checking.set(true);
    this.error.set(null);
    this.result.set(null);
    this.svc.verify().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (r) => { this.result.set(r); this.checking.set(false); },
      error: (e) => { this.error.set(extractProblem(e).detail); this.checking.set(false); },
    });
  }

  describe(r: AuditVerifyResponse): string {
    const at = r.brokenAtSequence;
    switch (r.failure) {
      case 'HashMismatch': return `Tampered record at sequence ${at}.`;
      case 'BrokenLink': return `Broken chain link at sequence ${at}.`;
      case 'SequenceGap': return `Missing record at sequence ${at}.`;
      case 'TailTruncated': return `Records deleted from the end of the chain (missing from sequence ${at}).`;
      case 'HeadMismatch': return `Chain head mismatch — the recorded head does not match the chain tail.`;
      default: return `The audit chain could not be verified.`;
    }
  }
}
