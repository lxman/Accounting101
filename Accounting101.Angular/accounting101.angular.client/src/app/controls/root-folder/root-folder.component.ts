import { Component } from '@angular/core';
import { DragDropModule, moveItemInArray, CdkDragDrop } from '@angular/cdk/drag-drop';
import { MatTreeModule, MatTreeNestedDataSource } from '@angular/material/tree';
import { AccountGroupModel } from '../../models/account-group.model';

@Component({
  selector: 'app-root-folder',
  imports: [],
  templateUrl: './root-folder.component.html',
  styleUrl: './root-folder.component.scss'
})

export class RootFolderComponent {
}
