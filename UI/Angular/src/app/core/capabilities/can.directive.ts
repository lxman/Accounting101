import { Directive, TemplateRef, ViewContainerRef, effect, inject, input } from '@angular/core';
import { CapabilityService } from './capability.service';

/**
 * Structural directive that renders its content only if the acting user holds the given capability
 * (or ANY of an array). Reactive to CapabilityService — the control appears/disappears as
 * capabilities resolve or the acting identity switches.
 *   <button *appCan="'ar.write'" ...>New invoice</button>
 *   <button *appCan="['gl.approve','gl.reverse']" ...>Approve</button>
 */
@Directive({ selector: '[appCan]', standalone: true })
export class CanDirective {
  private readonly tpl = inject(TemplateRef<unknown>);
  private readonly vcr = inject(ViewContainerRef);
  private readonly caps = inject(CapabilityService);

  readonly appCan = input.required<string | string[]>();

  private rendered = false;

  constructor() {
    effect(() => {
      const req = this.appCan();
      const ok = Array.isArray(req) ? req.some((c) => this.caps.has(c)) : this.caps.has(req);
      if (ok && !this.rendered) {
        this.vcr.createEmbeddedView(this.tpl);
        this.rendered = true;
      } else if (!ok && this.rendered) {
        this.vcr.clear();
        this.rendered = false;
      }
    });
  }
}
