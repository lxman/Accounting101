import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import {MenuItem} from '../../models/menu-item.interface';
import {Screen} from '../../enums/screen.enum';

@Injectable({
  providedIn: 'root',
})
export class MenuService {
  constructor(private http: HttpClient) {}

  getMenuItemsFor(screen: Screen): Observable<MenuItem[]> {
    // Example for fetching from API
    // return this.http.get<MenuItem[]>('/api/menu');

    // Example for static data
    const menuItems: MenuItem[] = [
      { label: 'Home', link: '/' },
      {
        label: 'Products',
        children: [
          { label: 'Electronics', link: '/products/electronics' },
          { label: 'Books', link: '/products/books' },
        ],
      },
      { label: 'Contact', link: '/contact' },
    ];
    return of(menuItems);
  }
}
