import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { HlmButton } from '@spartan-ng/helm/button';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { CapabilitySetService } from '../../core/capability-sets/capability-set.service';
import { CapabilitySet } from '../../core/capability-sets/capability-set';

@Component({
  selector: 'app-capability-set-list',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, ...HlmTableImports],
  template: `
    <div class="flex items-center justify-between mb-4">
      <h1 class="text-xl font-semibold">Capability Sets</h1>
      <button hlmBtn (click)="create()">New set</button>
    </div>
    @if (error()) { <p class="text-red-600 mb-2">{{ error() }}</p> }
    <div hlmTableContainer>
      <table hlmTable>
        <thead hlmTHead>
          <tr hlmTr><th hlmTh>Name</th><th hlmTh>Capabilities</th><th hlmTh>Members</th><th hlmTh></th></tr>
        </thead>
        <tbody hlmTBody>
          @for (s of sets(); track s.id) {
            <tr hlmTr class="cursor-pointer" (click)="edit(s)">
              <td hlmTd>{{ s.name }} @if (s.builtin) { <span class="text-xs text-muted-foreground">(built-in)</span> }</td>
              <td hlmTd>{{ s.capabilities.length }}</td>
              <td hlmTd>{{ s.affectedMemberCount }}</td>
              <td hlmTd>
                <button hlmBtn variant="destructive" size="sm"
                        (click)="$event.stopPropagation(); remove(s)">Delete</button>
              </td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class CapabilitySetList {
  private readonly service = inject(CapabilitySetService);
  private readonly router = inject(Router);
  protected readonly sets = signal<CapabilitySet[]>([]);
  protected readonly error = signal<string | null>(null);

  constructor() { this.reload(); }

  private reload(): void {
    this.service.list().subscribe({ next: (s) => this.sets.set(s), error: () => this.error.set('Failed to load sets.') });
  }

  protected create(): void { void this.router.navigate(['/admin/access/sets/new']); }
  protected edit(s: CapabilitySet): void { void this.router.navigate(['/admin/access/sets', s.id]); }

  remove(s: CapabilitySet): void {
    this.error.set(null);
    this.service.remove(s.id).subscribe({
      next: () => this.reload(),
      error: (e) => this.error.set(e?.error?.detail ?? `Cannot delete "${s.name}".`),
    });
  }
}
