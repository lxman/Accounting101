import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { HlmTableImports } from '@spartan-ng/helm/table';
import { MemberService } from '../../core/members/member.service';
import { Member } from '../../core/members/member';
import { memberDisplayName } from '../../core/api/dev-identity-names';
import { extractProblem } from '../../core/api/problem-details';

@Component({
  selector: 'app-member-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmTableImports],
  template: `
    <div class="flex flex-col gap-4 p-4">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Users &amp; Roles</h1>
      </div>

      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }
      @if (members().length === 0 && !error()) {
        <p class="text-muted-foreground text-sm">No members yet.</p>
      } @else {
        <div hlmTableContainer>
          <table hlmTable>
            <thead hlmTHead>
              <tr hlmTr>
                <th hlmTh>Member</th><th hlmTh>Roles</th><th hlmTh>Capabilities</th>
              </tr>
            </thead>
            <tbody hlmTBody>
              @for (m of members(); track m.userId) {
                <tr hlmTr class="cursor-pointer hover:bg-muted/50" role="button" tabindex="0"
                    (click)="open(m.userId)" (keydown.enter)="open(m.userId)">
                  <td hlmTd>{{ displayName(m.userId) }}</td>
                  <td hlmTd>{{ m.roles.join(', ') }}</td>
                  <td hlmTd class="tabular-nums">{{ m.capabilities.length }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
})
export class MemberList {
  private readonly svc = inject(MemberService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly members = signal<Member[]>([]);
  readonly error = signal<string | null>(null);

  constructor() {
    this.svc.list().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (m) => this.members.set(m),
      error: (e) => this.error.set(extractProblem(e).detail),
    });
  }

  displayName(userId: string): string { return memberDisplayName(userId); }
  open(userId: string): void { void this.router.navigate(['/admin/users', userId]); }
}
