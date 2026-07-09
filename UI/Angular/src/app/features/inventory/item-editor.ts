import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { InventoryService } from '../../core/inventory/inventory.service';
import { SaveItemRequest } from '../../core/inventory/inventory';
import { extractProblem } from '../../core/api/problem-details';
import { CanDirective } from '../../core/capabilities/can.directive';

@Component({
  selector: 'app-item-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton, CanDirective],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-2xl">
      <h1 class="text-2xl font-bold">{{ editId() ? 'Edit item' : 'New item' }}</h1>

      <div class="grid grid-cols-2 gap-4">
        <div class="flex flex-col gap-1">
          <label hlmLabel>SKU</label>
          <input hlmInput type="text" [value]="sku()" (input)="sku.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Name</label>
          <input hlmInput type="text" [value]="name()" (input)="name.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1">
          <label hlmLabel>Unit of measure</label>
          <input hlmInput type="text" [value]="unitOfMeasure()" (input)="unitOfMeasure.set($any($event.target).value)" />
        </div>
        <div class="flex flex-col gap-1 col-span-2">
          <label hlmLabel>Description</label>
          <input hlmInput type="text" [value]="description() ?? ''" (input)="description.set($any($event.target).value || null)" />
        </div>
      </div>

      @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }

      <div class="flex items-center gap-2">
        <button *appCan="'inventory.write'" hlmBtn type="button" (click)="save()" [disabled]="!canSave() || busy()">
          {{ editId() ? 'Save' : 'Create item' }}
        </button>
        <a hlmBtn variant="outline" routerLink="/inventory">Cancel</a>
      </div>
    </div>
  `,
})
export class ItemEditor {
  private readonly svc = inject(InventoryService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly editId = signal<string | null>(this.route.snapshot.paramMap.get('id'));
  readonly sku = signal('');
  readonly name = signal('');
  readonly description = signal<string | null>(null);
  readonly unitOfMeasure = signal('');
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);

  readonly canSave = computed(() =>
    this.sku().trim().length > 0 && this.name().trim().length > 0 && this.unitOfMeasure().trim().length > 0);

  constructor() {
    const id = this.editId();
    if (id) {
      this.svc.getItem(id).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
        next: (v) => {
          this.sku.set(v.item.sku); this.name.set(v.item.name);
          this.description.set(v.item.description); this.unitOfMeasure.set(v.item.unitOfMeasure);
        },
        error: (e) => this.message.set(extractProblem(e).detail),
      });
    }
  }

  private body(): SaveItemRequest {
    return { sku: this.sku().trim(), name: this.name().trim(), description: this.description(), unitOfMeasure: this.unitOfMeasure().trim() };
  }

  save(): void {
    if (!this.canSave()) return;
    this.busy.set(true); this.message.set(null);
    const id = this.editId();
    const call = id ? this.svc.updateItem(id, this.body()) : this.svc.createItem(this.body());
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (v) => { this.busy.set(false); void this.router.navigate(['/inventory/items', v.item.id]); },
      error: (e) => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }
}
