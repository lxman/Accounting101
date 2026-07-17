import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { ApprovalPolicyService } from '../../core/approval-policy/approval-policy.service';
import { ApprovalMode } from '../../core/approval-policy/approval-policy';
import { CapabilityService } from '../../core/capabilities/capability.service';

interface ModeOption { value: ApprovalMode; label: string; description: string; lowControl?: boolean; }

@Component({
  selector: 'app-approval-policy',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective, RouterLink],
  template: `
    <h1 class="text-xl font-semibold mb-4">Approval policy</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    @if (saved()) { <p class="text-green-600 mb-2">Saved.</p> }
    <p class="text-sm text-muted-foreground mb-3">Controls how journal entries and subledger postings
       reach the books for this client.</p>

    <div class="space-y-3">
      @for (o of options; track o.value) {
        <label class="flex items-start gap-3" [class.opacity-60]="isAutoApproveBlocked(o.value)">
          <input type="radio" name="mode" [value]="o.value" [checked]="selected() === o.value"
                 [disabled]="isAutoApproveBlocked(o.value)"
                 (change)="select(o.value)" class="mt-1" />
          <span>
            <span class="font-medium">{{ o.label }}</span>
            @if (o.lowControl) {
              <span class="ms-2 text-xs rounded bg-amber-100 text-amber-800 px-1.5 py-0.5">removes a review step</span>
            }
            <span class="block text-sm text-muted-foreground">{{ o.description }}</span>
            @if (o.value === 'AutoApprove' && pendingApprovalCount() > 0) {
              <span class="block text-sm text-amber-700 mt-1" data-testid="pending-note">
                {{ pendingCountText() }} awaiting approval. Clear the
                <a routerLink="/journal/approvals" class="underline">approval queue</a>
                before enabling auto-approve.
              </span>
            }
          </span>
        </label>
      }
    </div>

    <div class="flex gap-2 mt-4">
      <button *appCan="'admin.approvalPolicy'" hlmBtn [disabled]="selected() === null" (click)="save()">Save</button>
    </div>
  `,
})
export class ApprovalPolicyScreen {
  private readonly service = inject(ApprovalPolicyService);
  private readonly caps = inject(CapabilityService);

  readonly options: ModeOption[] = [
    { value: 'TwoPerson', label: 'Two-person approval',
      description: 'An entry must be approved by someone other than its author (segregation of duties).' },
    { value: 'SelfApprove', label: 'Self-approve',
      description: 'The author may approve their own entries.' },
    { value: 'AutoApprove', label: 'Auto-approve',
      description: 'Entries reach the books at post time. No second review; still fully audited.', lowControl: true },
  ];

  readonly selected = signal<ApprovalMode | null>(null);
  readonly error = signal<string | null>(null);
  readonly saved = signal(false);
  readonly pendingApprovalCount = signal(0);

  constructor() {
    this.service.get().subscribe({
      next: (p) => { this.selected.set(p.mode); this.pendingApprovalCount.set(p.pendingApprovalCount ?? 0); },
      error: () => this.error.set('Could not load the approval policy.'),
    });
  }

  isAutoApproveBlocked(mode: ApprovalMode): boolean {
    return mode === 'AutoApprove' && this.pendingApprovalCount() > 0;
  }

  pendingCountText(): string {
    const n = this.pendingApprovalCount();
    return n === 1 ? '1 entry is' : `${n} entries are`;
  }

  select(mode: ApprovalMode): void {
    if (this.isAutoApproveBlocked(mode)) return;
    this.selected.set(mode);
    this.saved.set(false);
  }

  save(): void {
    const mode = this.selected();
    if (!mode) return;
    this.error.set(null);
    this.service.set(mode).subscribe({
      next: (p) => { this.saved.set(true); this.pendingApprovalCount.set(p.pendingApprovalCount ?? 0); this.caps.reload(); },
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }
}
