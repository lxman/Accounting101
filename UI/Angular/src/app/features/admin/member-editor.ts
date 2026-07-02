import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { MemberService } from '../../core/members/member.service';
import { CapabilityCatalog } from '../../core/members/member';
import { memberDisplayName } from '../../core/api/dev-identity-names';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';

interface CapabilityGroup { area: string; capabilities: string[]; }

@Component({
  selector: 'app-member-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, CanDirective, ...HlmInputImports, ...HlmLabelImports, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">{{ editId ? 'Edit member' : 'New member' }}</h1>

      @if (editId) {
        <div class="flex flex-col gap-1">
          <label hlmLabel>Member</label>
          <input hlmInput type="text" [value]="displayName()" readonly disabled />
        </div>
      } @else {
        <div class="flex flex-col gap-1">
          <label hlmLabel>User id</label>
          <input hlmInput type="text" [value]="userId()" (input)="userId.set($any($event.target).value)" />
        </div>
      }

      @if (catalog(); as cat) {
        <div class="flex flex-col gap-2">
          <h2 class="text-lg font-semibold">Roles</h2>
          @for (r of cat.roles; track r.role) {
            <label class="flex items-center gap-2 text-sm">
              <input type="checkbox" [checked]="checkedRoles().has(r.role)" (change)="togglePreset(r)" />
              {{ r.role }}
            </label>
          }
        </div>

        <div class="flex flex-col gap-3">
          <h2 class="text-lg font-semibold">Capabilities</h2>
          @for (g of groups(); track g.area) {
            <div class="flex flex-col gap-1">
              <h3 class="text-sm font-medium text-muted-foreground">{{ g.area }}</h3>
              @for (c of g.capabilities; track c) {
                <label class="flex items-center gap-2 text-sm ps-2">
                  <input type="checkbox" [checked]="capabilities().has(c)" (change)="toggleCapability(c)" />
                  {{ c }}
                </label>
              }
            </div>
          }
        </div>
      }

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
      <div class="flex items-center gap-2">
        <button *appCan="'admin.users'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">Save</button>
        <a hlmBtn variant="outline" routerLink="/admin/users">Cancel</a>
        @if (editId) {
          <button *appCan="'admin.users'" hlmBtn variant="destructive" type="button" class="ms-auto" (click)="remove()" [disabled]="busy()">Remove</button>
        }
      </div>
    </div>
  `,
})
export class MemberEditor {
  private readonly svc = inject(MemberService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly editId = this.route.snapshot.paramMap.get('userId'); // null on /admin/users/new

  readonly userId = signal('');
  readonly catalog = signal<CapabilityCatalog | null>(null);
  readonly capabilities = signal<Set<string>>(new Set());
  readonly checkedRoles = signal<Set<string>>(new Set());
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly groups = computed<CapabilityGroup[]>(() => {
    const cat = this.catalog();
    if (!cat) return [];
    const byArea = new Map<string, string[]>();
    for (const c of cat.capabilities) {
      const area = c.includes('.') ? c.slice(0, c.indexOf('.')) : c;
      const list = byArea.get(area) ?? [];
      list.push(c);
      byArea.set(area, list);
    }
    return [...byArea.entries()].map(([area, capabilities]) => ({ area, capabilities }));
  });

  readonly canSave = computed(() => this.editId !== null || this.userId().trim().length > 0);

  constructor() {
    this.svc.catalog().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (cat) => this.catalog.set(cat),
      error: (e) => this.message.set(extractProblem(e).detail),
    });

    if (this.editId) {
      this.svc.list().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (members) => {
          const existing = members.find((m) => m.userId === this.editId);
          if (existing) {
            this.capabilities.set(new Set(existing.capabilities));
            this.checkedRoles.set(new Set(existing.roles));
          }
        },
        error: (e) => this.message.set(extractProblem(e).detail),
      });
    }
  }

  displayName(): string { return this.editId ? memberDisplayName(this.editId) : ''; }

  togglePreset(preset: { role: string; capabilities: string[] }): void {
    const roles = new Set(this.checkedRoles());
    if (roles.has(preset.role)) {
      roles.delete(preset.role);
      this.checkedRoles.set(roles);
      return;
    }
    roles.add(preset.role);
    this.checkedRoles.set(roles);
    const caps = new Set(this.capabilities());
    for (const c of preset.capabilities) caps.add(c);
    this.capabilities.set(caps);
  }

  toggleCapability(capability: string): void {
    const caps = new Set(this.capabilities());
    if (caps.has(capability)) caps.delete(capability); else caps.add(capability);
    this.capabilities.set(caps);
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true);
    this.message.set(null);
    const roles = [...this.checkedRoles()];
    const capabilities = [...this.capabilities()];
    const obs = this.editId
      ? this.svc.set(this.editId, { roles, capabilities })
      : this.svc.add({ userId: this.userId().trim(), roles, capabilities });
    obs.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/admin/users']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  remove(): void {
    if (!this.editId) return;
    if (!confirm(`Remove ${this.displayName()} from this client?`)) return;
    this.busy.set(true);
    this.message.set(null);
    this.svc.remove(this.editId).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => { this.busy.set(false); void this.router.navigate(['/admin/users']); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
