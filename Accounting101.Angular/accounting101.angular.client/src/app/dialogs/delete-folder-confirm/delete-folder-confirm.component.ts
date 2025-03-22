import {Component, inject} from '@angular/core';
import {MatButton} from "@angular/material/button";
import {MatDialogActions, MatDialogContent, MatDialogRef, MatDialogTitle} from "@angular/material/dialog";

@Component({
  selector: 'app-delete-folder-confirm',
  imports: [
    MatButton,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle
  ],
  templateUrl: './delete-folder-confirm.component.html',
  styleUrl: './delete-folder-confirm.component.scss'
})
export class DeleteFolderConfirm {
  readonly dialogRef = inject(MatDialogRef<DeleteFolderConfirm>);

  onNoClicked() {
    this.dialogRef.close();
  }

  onYesClicked() {
    this.dialogRef.close(true);
  }
}
