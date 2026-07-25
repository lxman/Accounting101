import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { HlmInputImports } from '@spartan-ng/helm/input';
import { HlmLabelImports } from '@spartan-ng/helm/label';
import { HlmButton } from '@spartan-ng/helm/button';
import { EInvoicingService } from '../../core/einvoicing/einvoicing.service';
import { FindingDto, InboundPreviewResponse, profileLabel } from '../../core/einvoicing/einvoicing';
import { extractProblem } from '../../core/api/problem-details';

type Stage = 'upload' | 'preview';

/**
 * Upload an EN 16931 e-invoice — a Factur-X/ZUGFeRD PDF, a CII XML, or a UBL XML — review what the
 * engine found, code each line, and post a draft bill through Payables' own API.
 *
 * The e-invoicing module is advisory and stateless: it writes nothing. Everything that turns a
 * proposal into a bill happens here.
 */
@Component({
  selector: 'app-einvoice-import',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, ...HlmInputImports, ...HlmLabelImports, HlmButton],
  template: `
    <div class="flex flex-col gap-4 p-4 max-w-5xl">
      <a routerLink="/payables/bills" class="text-sm text-muted-foreground hover:text-foreground">← Bills</a>
      <h1 class="text-2xl font-bold">Import e-invoice</h1>

      @if (stage() === 'upload') {
        <p class="text-sm text-muted-foreground max-w-prose">
          Upload a Factur-X / ZUGFeRD PDF, or a CII or UBL XML invoice. Nothing is saved until you
          create the bill.
        </p>
        <div class="flex flex-col gap-1 max-w-md">
          <label hlmLabel>File</label>
          <input hlmInput type="file" accept=".pdf,.xml,application/pdf,text/xml,application/xml"
                 (change)="onFile($event)" />
        </div>
        @if (message()) { <p class="text-destructive text-sm">{{ message() }}</p> }
        <div>
          <button hlmBtn type="button" (click)="upload()" [disabled]="!canUpload() || busy()">
            Upload &amp; preview
          </button>
        </div>
      }

      @if (stage() === 'preview') {
        @if (preview(); as p) {
          <div class="flex items-center gap-2 flex-wrap text-sm">
            <span class="font-medium">{{ file()?.name }}</span>
            <span class="rounded border border-border px-2 py-0.5 text-xs">{{ p.syntax }}</span>
            <span class="rounded border border-border px-2 py-0.5 text-xs">{{ profile() }}</span>
          </div>

          @if (errors().length) {
            <div class="border border-destructive rounded p-3 flex flex-col gap-2">
              <p class="font-semibold text-destructive">Errors</p>
              <ul class="flex flex-col gap-1">
                @for (f of errors(); track $index) {
                  <li class="text-sm">
                    <code class="text-xs bg-muted px-1 rounded">{{ f.ruleId }}</code>
                    <span class="ms-1">{{ f.message }}</span>
                    @if (f.businessTerm) {
                      <span class="text-muted-foreground text-xs ms-1">({{ f.businessTerm }})</span>
                    }
                    @if (f.location) { <div class="text-muted-foreground text-xs">{{ f.location }}</div> }
                  </li>
                }
              </ul>
            </div>
          }

          @if (otherCount()) {
            <div class="border border-border rounded p-3 flex flex-col gap-2">
              <button hlmBtn type="button" variant="ghost" size="sm" class="self-start"
                      (click)="showOtherFindings.set(!showOtherFindings())">
                {{ showOtherFindings() ? 'Hide' : 'Show' }} {{ otherCount() }} advisory finding{{ otherCount() === 1 ? '' : 's' }}
              </button>
              @if (showOtherFindings()) {
                <ul class="flex flex-col gap-1">
                  @for (f of advisories(); track $index) {
                    <li class="text-sm">
                      <code class="text-xs bg-muted px-1 rounded">{{ f.ruleId }}</code>
                      <span class="ms-1 text-muted-foreground text-xs">{{ f.severity }}</span>
                      <span class="ms-1">{{ f.message }}</span>
                      @if (f.businessTerm) {
                        <span class="text-muted-foreground text-xs ms-1">({{ f.businessTerm }})</span>
                      }
                    </li>
                  }
                </ul>
              }
            </div>
          }

          @if (!p.canImport) {
            <p class="text-sm text-muted-foreground max-w-prose">
              This document can't become a bill. Fix it with the vendor, or upload a different file.
            </p>
            <div>
              <button hlmBtn type="button" variant="outline" (click)="reset()">Choose another file</button>
            </div>
          }
        }
      }
    </div>
  `,
})
export class EInvoiceImport {
  private readonly svc = inject(EInvoicingService);
  private readonly destroyRef = inject(DestroyRef);

  readonly stage = signal<Stage>('upload');
  readonly file = signal<File | null>(null);
  readonly preview = signal<InboundPreviewResponse | null>(null);
  readonly busy = signal(false);
  readonly message = signal<string | null>(null);
  readonly showOtherFindings = signal(false);

  readonly canUpload = computed(() => !!this.file());
  readonly profile = computed(() => profileLabel(this.preview()?.profile ?? null));
  readonly errors = computed(() => this.findingsOf('Error'));
  /** Warnings and information, in that order — advisory, never blocking. */
  readonly advisories = computed(() => [...this.findingsOf('Warning'), ...this.findingsOf('Information')]);
  readonly otherCount = computed(() => this.advisories().length);

  private findingsOf(severity: FindingDto['severity']): FindingDto[] {
    return (this.preview()?.findings ?? []).filter(f => f.severity === severity);
  }

  onFile(e: Event): void {
    const picked = (e.target as HTMLInputElement).files?.[0] ?? null;
    this.file.set(picked);
    this.preview.set(null);
    this.showOtherFindings.set(false);
    this.message.set(picked && picked.size > EInvoicingService.maxUploadBytes ? this.tooLarge() : null);
  }

  upload(): void {
    const f = this.file();
    if (!f || this.busy()) return;
    if (f.size > EInvoicingService.maxUploadBytes) { this.message.set(this.tooLarge()); return; }
    this.busy.set(true);
    this.message.set(null);
    this.svc.previewInbound(f).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => { this.preview.set(r); this.stage.set('preview'); this.busy.set(false); },
      error: e => { this.message.set(extractProblem(e).detail); this.busy.set(false); },
    });
  }

  reset(): void {
    this.stage.set('upload');
    this.preview.set(null);
    this.file.set(null);
    this.message.set(null);
    this.showOtherFindings.set(false);
  }

  private tooLarge(): string { return 'That file is larger than the 15 MB limit.'; }
}
