import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { CdkDrag, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { HlmButton } from '@spartan-ng/helm/button';
import { AccountsService } from '../../core/accounts/accounts.service';
import { AccountResponse } from '../../core/accounts/account';
import { TrialBalanceService } from '../../core/trial-balance/trial-balance.service';
import { buildTree, canDrop, TypeSection } from '../../core/accounts/account-tree';
import { extractProblem } from '../../core/api/problem-details';
import { money } from '../../core/format/display';
import { CanDirective } from '../../core/capabilities/can.directive';
import { CapabilityService } from '../../core/capabilities/capability.service';

@Component({
  selector: 'app-chart-of-accounts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, RouterLink, CdkDropListGroup, CdkDropList, CdkDrag, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-3xl">
      <div class="flex items-center gap-3">
        <h1 class="text-2xl font-bold">Chart of Accounts</h1>
        <a *appCan="'gl.manageAccounts'" hlmBtn size="sm" routerLink="/accounts/new" class="ms-auto">New account</a>
        <label class="text-sm text-muted-foreground flex items-center gap-1">
          <input type="checkbox" [checked]="showInactive()" (change)="showInactive.set($any($event.target).checked)" /> Show inactive
        </label>
      </div>
      <p class="text-xs text-muted-foreground">Drag an account onto another (same type) to re-parent it, or onto a section header to make it a root. Order follows the account number — edit a number to reorder.</p>
      @if (error()) { <p class="text-destructive text-sm">{{ error() }}</p> }

      <div cdkDropListGroup class="coa-dnd flex flex-col gap-4">
        @for (section of sections(); track section.type) {
          <section>
            <h2 class="font-semibold text-sm uppercase text-muted-foreground border-b border-border pb-1 mb-1"
                cdkDropList [cdkDropListData]="{ type: section.type, parentId: null }"
                [cdkDropListSortingDisabled]="true"
                [cdkDropListEnterPredicate]="enterPredicate(section.type, null)"
                (cdkDropListDropped)="dropped($event, section.type, null)">{{ section.type }}</h2>
            @for (node of section.nodes; track node.account.id) {
              <ng-container [ngTemplateOutlet]="row" [ngTemplateOutletContext]="{ node, type: section.type, depth: 0 }" />
            }
            @if (section.nodes.length === 0) { <p class="text-xs text-muted-foreground italic">No accounts.</p> }
          </section>
        }

      <!-- Declared INSIDE cdkDropListGroup so the recursive row drop-lists inherit the
           group from their declaration injector and register as connected drop targets.
           (ngTemplateOutlet resolves directive DI from the template's declaration site.) -->
      <ng-template #row let-node="node" let-type="type" let-depth="depth">
        <!-- Drop target: dropping an account here re-parents it under this node.
             cdkDrag must live INSIDE a cdkDropList for CDK to fire drop events. -->
        <div cdkDropList [cdkDropListData]="{ type, parentId: node.account.id }"
             [cdkDropListSortingDisabled]="true"
             [cdkDropListEnterPredicate]="enterPredicate(type, node.account.id)"
             (cdkDropListDropped)="dropped($event, type, node.account.id)">
          <div class="flex items-center gap-2 py-1 border-b border-border/50 text-sm cursor-grab active:cursor-grabbing"
               [style.padding-left.rem]="depth"
               cdkDrag [cdkDragData]="node.account.id" [cdkDragDisabled]="!caps.has('gl.manageAccounts')"
               [class.opacity-50]="!node.account.active">
            <span class="font-mono">{{ node.account.number }}</span>
            <span> {{ node.account.name }}</span>
            @if (!node.account.postable) { <span class="text-xs px-1 rounded bg-muted text-muted-foreground">header</span> }
            @if (!node.account.active) { <span class="text-xs px-1 rounded bg-muted text-muted-foreground">inactive</span> }
            <span class="ms-auto tabular-nums">{{ money(node.balance) }}</span>
            <a *appCan="'gl.manageAccounts'" class="underline text-xs" [routerLink]="['/accounts', node.account.id, 'edit']">Edit</a>
          </div>
        </div>
        @for (child of node.children; track child.account.id) {
          <ng-container [ngTemplateOutlet]="row" [ngTemplateOutletContext]="{ node: child, type, depth: depth + 1 }" />
        }
      </ng-template>
      </div>
    </div>
  `,
})
export class ChartOfAccounts {
  private readonly accountsSvc = inject(AccountsService);
  private readonly trialBalance = inject(TrialBalanceService);
  readonly caps = inject(CapabilityService);

  readonly showInactive = signal(false);
  readonly error = signal<string | null>(null);
  private readonly balances = signal<ReadonlyMap<string, number>>(new Map());

  readonly sections = computed<TypeSection[]>(() =>
    buildTree(this.accountsSvc.accounts(), this.balances(), this.showInactive()));

  constructor() {
    this.accountsSvc.load();
    this.trialBalance.get().subscribe({
      next: (tb) => this.balances.set(new Map(tb.accounts.map(a => [a.accountId, a.balance]))),
      error: () => { /* balances are annotation only; tree still renders without them */ },
    });
  }

  money(n: number): string { return money(n); }

  enterPredicate(type: AccountResponse['type'], parentId: string | null) {
    return (drag: { data: string }) => canDrop(this.accountsSvc.accounts(), drag.data, parentId, type);
  }

  dropped(event: { item: { data: string } }, type: AccountResponse['type'], parentId: string | null): void {
    this.onDrop(event.item.data, parentId, type);
  }

  // Pulled out for direct unit testing (the CDK event is awkward to synthesize).
  onDrop(draggedId: string, newParentId: string | null, sectionType: AccountResponse['type']): void {
    if (!this.caps.has('gl.manageAccounts')) return;
    if (!canDrop(this.accountsSvc.accounts(), draggedId, newParentId, sectionType)) return;
    const a = this.accountsSvc.byId().get(draggedId);
    if (!a || a.parentId === newParentId) return;
    this.error.set(null);
    this.accountsSvc.upsert({
      id: a.id, number: a.number, name: a.name, type: a.type, parentId: newParentId,
      postable: a.postable, requiredDimension: a.requiredDimension, cashFlowActivity: a.cashFlowActivity,
      isRetainedEarnings: a.isRetainedEarnings, active: a.active,
    }).subscribe({ error: (e) => this.error.set(extractProblem(e).detail) });
  }
}
