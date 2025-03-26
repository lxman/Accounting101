// Originally from https://nartc.me/blog/typed-mat-cell-def/
// Condensed into a gist @ https://gist.github.com/R1D3R175/087a605ff3c748b019f73e9a96e730b8
import { CdkCellDef } from "@angular/cdk/table";
import { Directive, Input } from "@angular/core";
import { MatCellDef, MatTable } from "@angular/material/table";

@Directive({
  selector: "[matCellDef]", // same selector as MatCellDef
  providers: [{ provide: CdkCellDef, useExisting: TypeSafeMatCellDef }],
})

export class TypeSafeMatCellDef<T> extends MatCellDef {
  // leveraging syntactic-sugar syntax when we use *matCellDef
  @Input() matCellDefTable?: MatTable<T>;

  // ngTemplateContextGuard flag to help with the Language Service
  static ngTemplateContextGuard<T>(
    dir: TypeSafeMatCellDef<T>,
    ctx: unknown,
  ): ctx is { $implicit: T; index: number } {
    return true;
  }
}
