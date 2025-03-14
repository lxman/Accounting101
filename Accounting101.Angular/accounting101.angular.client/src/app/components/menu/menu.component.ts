import { Component } from '@angular/core';
import {MatMenu, MatMenuItem, MatMenuTrigger} from '@angular/material/menu';
import {FlexLayoutModule} from '@angular/flex-layout';

@Component({
  selector: 'app-menu',
  imports: [
    MatMenu,
    MatMenuTrigger,
    MatMenuItem,
    FlexLayoutModule,
  ],
  templateUrl: './menu.component.html',
  styleUrl: './menu.component.scss'
})

export class MenuComponent {

}
