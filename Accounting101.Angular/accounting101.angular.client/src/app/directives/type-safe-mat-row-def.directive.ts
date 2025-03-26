// Taken from: https://nartc.me/blog/typed-mat-cell-def/
// Condensed into a gist @ https://gist.github.com/R1D3R175/087a605ff3c748b019f73e9a96e730b8
import {CdkCellDef} from '@angular/cdk/table';
import {Directive, Input} from '@angular/core';
import {MatRowDef, MatTable} from '@angular/material/table';
import {TypeSafeMatCellDef} from './type-safe-mat-cell-def.directive';

@Directive({
  selector: '[matRowDef]',
  providers: [{provide: CdkCellDef, useExisting: TypeSafeMatCellDef}]
})
export class TypeSafeMatRowDef<T> extends MatRowDef<T> {
  @Input() matRowDefTable?: MatTable<T>;

  static ngTemplateContextGuard<T>(
    dir: TypeSafeMatRowDef<T>,
    ctx: unknown,
  ): ctx is {$implicit: T; index: number} {
    return true;
  }
}
