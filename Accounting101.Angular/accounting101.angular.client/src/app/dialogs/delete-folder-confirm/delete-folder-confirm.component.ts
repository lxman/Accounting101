import {Component, inject, ViewChild, AfterViewInit} from '@angular/core';
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

export class DeleteFolderConfirm implements AfterViewInit{
  readonly dialogRef = inject(MatDialogRef<DeleteFolderConfirm>);

  @ViewChild('yesButton', {static: false}) yesButton!: MatButton;

  ngAfterViewInit() {
    if (this.yesButton?._elementRef) {
      this.yesButton._elementRef.nativeElement.focus();
    }
  }

  onNoClicked() {
    this.dialogRef.close();
  }

  onYesClicked() {
    this.dialogRef.close(true);
  }
}
