import {Component, inject, input, OnChanges, SimpleChanges} from '@angular/core';
import { MenuService } from '../../services/menu/menu.service';
import { MenuItem } from '../../models/menu-item.interface';
import {MatMenu, MatMenuItem, MatMenuTrigger} from '@angular/material/menu';
import {MatButton} from '@angular/material/button';
import {NgForOf, NgIf} from '@angular/common';
import {RouterLink} from '@angular/router';
import {Screen} from '../../enums/screen.enum';
import {FlexLayoutModule} from '@ngbracket/ngx-layout';
import {UserClient} from '../../clients/user-client/user-client.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {Router} from '@angular/router';

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
    NgIf,
    FlexLayoutModule
  ]
})

export class MenuComponent implements OnChanges {
  private readonly menuService: MenuService = inject(MenuService);
  private readonly userManager: UserClient = inject(UserClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly router: Router = inject(Router);
  readonly screen = input.required<Screen>();
  menuItems: MenuItem[] = [];

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['screen'].firstChange) {
      this.menuService.getMenuItemsFor(this.screen()).subscribe((items) => {
        this.menuItems = items;
      });
    }
  }

  logoutClicked(): void {
    this.userManager.logoutUser();
    this.userData.clearData();
    void this.router.navigate(['/']);
  }
}
