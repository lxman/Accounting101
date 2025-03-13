import { Component } from '@angular/core';
import {MatMenu, MatMenuItem, MatMenuTrigger} from '@angular/material/menu';
import {MatAnchor, MatButton} from '@angular/material/button';
import {FlexLayoutModule} from '@angular/flex-layout';

@Component({
  selector: 'app-menu',
  imports: [
    MatMenu,
    MatButton,
    MatMenuTrigger,
    MatMenuItem,
    FlexLayoutModule,
    MatAnchor
  ],
  templateUrl: './menu.component.html',
  styleUrl: './menu.component.scss'
})

export class MenuComponent {

}
