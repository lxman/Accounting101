import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { CanDirective } from '../../core/capabilities/can.directive';
import { CapabilitySetService } from '../../core/capability-sets/capability-set.service';
import { CapabilitySet } from '../../core/capability-sets/capability-set';
import { MemberService } from '../../core/members/member.service';

@Component({
  selector: 'app-member-editor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, CanDirective],
  template: `
    <h1 class="text-xl font-semibold mb-4">Member access</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    <p class="text-sm text-muted-foreground mb-3">Assign one or more capability sets. The member's
       access is the union of the selected sets, applied immediately.</p>

    <div class="space-y-2">
      @for (s of sets(); track s.id) {
        <label class="flex items-center gap-2">
          <input type="checkbox" [checked]="selected().has(s.id)" (change)="toggleSet(s.id)" />
          <span>{{ s.name }} @if (s.builtin) { <span class="text-xs text-muted-foreground">(built-in)</span> }</span>
        </label>
      }
    </div>

    <div class="flex gap-2 mt-4">
      <button *appCan="'admin.users'" hlmBtn (click)="save()">Save</button>
      <button hlmBtn variant="outline" (click)="back()">Cancel</button>
    </div>
  `,
})
export class MemberEditor {
  private readonly setService = inject(CapabilitySetService);
  private readonly members = inject(MemberService);
  private readonly router = inject(Router);
  readonly userId = inject(ActivatedRoute).snapshot.paramMap.get('userId');

  readonly sets = signal<CapabilitySet[]>([]);
  readonly selected = signal<Set<string>>(new Set());
  readonly error = signal<string | null>(null);

  constructor() {
    this.setService.list().subscribe({ next: (s) => this.sets.set(s) });
    if (this.userId) {
      this.members.list().subscribe({
        next: (members) => {
          const me = members.find((m) => m.userId === this.userId);
          if (me) this.selected.set(new Set(me.grantedSetIds));
        },
      });
    }
  }

  toggleSet(id: string): void {
    const next = new Set(this.selected());
    next.has(id) ? next.delete(id) : next.add(id);
    this.selected.set(next);
  }

  save(): void {
    if (!this.userId) return;
    this.error.set(null);
    this.members.assignSets(this.userId, { setIds: [...this.selected()] }).subscribe({
      next: () => this.back(),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }

  back(): void { void this.router.navigate(['/admin/users']); }
}
