import {ChangeDetectionStrategy, Component, inject, model} from '@angular/core';
import {MatFormFieldModule} from '@angular/material/form-field';
import {MatInputModule} from '@angular/material/input';
import {FormsModule} from '@angular/forms';
import {MatButtonModule} from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle
} from '@angular/material/dialog';

@Component({
  selector: 'app-get-folder-name',
  templateUrl: './get-folder-name.component.html',
  imports: [
    MatFormFieldModule,
    MatInputModule,
    FormsModule,
    MatButtonModule,
    MatDialogTitle,
    MatDialogContent,
    MatDialogActions,
    MatDialogClose
  ],
  changeDetection: ChangeDetectionStrategy.OnPush
})

export class GetFolderName {
  readonly dialogRef = inject(MatDialogRef<GetFolderName>);
  readonly data = inject<GetFolderNameData>(MAT_DIALOG_DATA);
  readonly name = model(this.data.name);

  enterPressed() {
    this.dialogRef.close(this.name());
  }

  onCancelClick() {
    this.dialogRef.close();
  }
}

export interface GetFolderNameData {
  name: string;
}
