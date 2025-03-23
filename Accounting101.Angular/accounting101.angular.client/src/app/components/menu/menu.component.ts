import {Component, inject, input, OnChanges, SimpleChanges} from '@angular/core';
import { MenuService } from '../../services/menu/menu.service';
import { MenuItem } from '../../models/menu-item.interface';
import {MatMenu, MatMenuItem, MatMenuTrigger} from '@angular/material/menu';
import {MatButton} from '@angular/material/button';
import {NgForOf, NgIf} from '@angular/common';
import {RouterLink} from '@angular/router';
import {Screen} from '../../enums/screen.enum';

@Component({
  selector: 'app-menu',
  templateUrl: `./menu.component.html`,
  imports: [
    MatMenuTrigger,
    MatButton,
    MatMenu,
    NgForOf,
    MatMenuItem,
    RouterLink,
    NgIf
  ]
})

export class MenuComponent implements OnChanges {
  private readonly menuService: MenuService = inject(MenuService);
  readonly screen = input.required<Screen>();
  menuItems: MenuItem[] = [];

  ngOnChanges(changes: SimpleChanges): void {
    if (!changes['screen'].firstChange) {
      this.menuService.getMenuItemsFor(this.screen()).subscribe((items) => {
        this.menuItems = items;
      });
    }
  }
}
