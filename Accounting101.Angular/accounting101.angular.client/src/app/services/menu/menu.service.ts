import {Injectable} from '@angular/core';
import {Observable, of} from 'rxjs';
import {MenuItem} from '../../models/menu-item.interface';
import {Screen} from '../../enums/screen.enum';

@Injectable({
  providedIn: 'root',
})
export class MenuService {
  getMenuItemsFor(screen: Screen): Observable<MenuItem[]> {
    switch (screen) {
      case Screen.clientSelector:
        return this.getClientSelectorMenuItems();
      case Screen.accountList:
        return this.getAccountListMenuItems();
      default:
        return of([]);
    }
  }

  getClientSelectorMenuItems(): Observable<MenuItem[]> {
    const menuItems: MenuItem[] = [
      { label: 'New Client', link: '/create-client' }
    ];
    return of(menuItems);
  }

  getAccountListMenuItems(): Observable<MenuItem[]> {
    const menuItems: MenuItem[] = [
      { label: 'Client List', link: '/client-selector'},
      { label: 'New Account', link: '/create-account' }
    ];
    return of(menuItems);
  }
}
