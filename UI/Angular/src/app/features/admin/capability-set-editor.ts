import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { CapabilitySetService } from '../../core/capability-sets/capability-set.service';
import { CapabilitySet } from '../../core/capability-sets/capability-set';
import { MemberService } from '../../core/members/member.service';

interface CapGroup { area: string; capabilities: string[]; }

@Component({
  selector: 'app-capability-set-editor',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, ...HlmInputImports, ...HlmLabelImports],
  template: `
    <h1 class="text-xl font-semibold mb-4">{{ editId ? 'Edit set' : 'New set' }}</h1>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }

    <label hlmLabel>Name
      <input hlmInput [value]="name()" (input)="setName($any($event.target).value)" />
    </label>
    <label hlmLabel class="block mt-3">Description
      <input hlmInput [value]="description()" (input)="setDescription($any($event.target).value)" />
    </label>

    <div class="mt-4 space-y-3">
      @for (g of groups(); track g.area) {
        <fieldset class="border rounded p-3">
          <legend class="text-sm font-medium">{{ g.area }}</legend>
          @for (cap of g.capabilities; track cap) {
            <label class="flex items-center gap-2">
              <input type="checkbox" [checked]="selected().has(cap)" (change)="toggleCapability(cap)" />
              <span>{{ cap }}</span>
            </label>
          }
        </fieldset>
      }
    </div>

    @if (confirming()) {
      <div class="mt-4 border border-amber-500 rounded p-3">
        <p>This set is held by <strong>{{ current()?.affectedMemberCount }}</strong> member(s).
           Applying these changes updates their access immediately.</p>
        <div class="flex gap-2 mt-2">
          <button hlmBtn (click)="confirmSave()">Apply changes</button>
          <button hlmBtn variant="outline" (click)="cancelConfirm()">Cancel</button>
        </div>
      </div>
    } @else {
      <div class="flex gap-2 mt-4">
        <button hlmBtn [disabled]="!name().trim()" (click)="save()">Save</button>
        <button hlmBtn variant="outline" (click)="back()">Cancel</button>
      </div>
    }
  `,
})
export class CapabilitySetEditor {
  private readonly service = inject(CapabilitySetService);
  private readonly members = inject(MemberService);
  private readonly router = inject(Router);
  protected readonly editId = inject(ActivatedRoute).snapshot.paramMap.get('id');

  readonly name = signal('');
  readonly description = signal('');
  readonly selected = signal<Set<string>>(new Set());
  readonly current = signal<CapabilitySet | null>(null);
  readonly confirming = signal(false);
  readonly error = signal<string | null>(null);
  private readonly catalog = signal<string[]>([]);

  protected readonly groups = computed<CapGroup[]>(() => {
    const byArea = new Map<string, string[]>();
    for (const cap of this.catalog()) {
      const area = cap.split('.')[0];
      byArea.set(area, [...(byArea.get(area) ?? []), cap]);
    }
    return [...byArea.entries()].map(([area, capabilities]) => ({ area, capabilities }));
  });

  constructor() {
    this.members.catalog().subscribe({ next: (c) => this.catalog.set(c.capabilities) });
    if (this.editId) {
      this.service.list().subscribe({
        next: (sets) => {
          const set = sets.find((s) => s.id === this.editId);
          if (!set) { this.error.set('Set not found.'); return; }
          this.current.set(set);
          this.name.set(set.name);
          this.description.set(set.description ?? '');
          this.selected.set(new Set(set.capabilities));
        },
      });
    }
  }

  setName(v: string): void { this.name.set(v); }
  setDescription(v: string): void { this.description.set(v); }
  toggleCapability(cap: string): void {
    const next = new Set(this.selected());
    next.has(cap) ? next.delete(cap) : next.add(cap);
    this.selected.set(next);
  }

  save(): void {
    this.error.set(null);
    // New set (or an edited set nobody holds) applies immediately; otherwise confirm the blast radius.
    if (this.editId && (this.current()?.affectedMemberCount ?? 0) > 0) { this.confirming.set(true); return; }
    this.persist();
  }
  confirmSave(): void { this.confirming.set(false); this.persist(); }
  cancelConfirm(): void { this.confirming.set(false); }

  private persist(): void {
    const req = {
      name: this.name().trim(),
      description: this.description().trim() || undefined,
      capabilities: [...this.selected()],
    };
    const call = this.editId ? this.service.update(this.editId, req) : this.service.create(req);
    call.subscribe({
      next: () => this.back(),
      error: (e) => this.error.set(e?.error?.detail ?? 'Save failed.'),
    });
  }

  back(): void { void this.router.navigate(['/admin/access/sets']); }
}
