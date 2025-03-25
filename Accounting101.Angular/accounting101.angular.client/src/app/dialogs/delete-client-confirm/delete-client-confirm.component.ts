import {AfterViewInit, Component, inject, ViewChild} from '@angular/core';
import {MatButton} from '@angular/material/button';
import {MatDialogActions, MatDialogContent, MatDialogRef, MatDialogTitle} from '@angular/material/dialog';

@Component({
  selector: 'app-delete-client-confirm',
  imports: [
    MatButton,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle
  ],
  templateUrl: './delete-client-confirm.component.html',
  styleUrl: './delete-client-confirm.component.scss'
})

export class DeleteClientConfirm implements AfterViewInit{
  readonly dialogRef = inject(MatDialogRef<DeleteClientConfirm>);

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
