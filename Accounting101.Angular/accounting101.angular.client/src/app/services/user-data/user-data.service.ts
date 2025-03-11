import { Injectable } from '@angular/core';

@Injectable({
  providedIn: 'root'
})

export class UserDataService {

  get(key: string): string {
    return sessionStorage.getItem(key) ?? '';
  }

  set(key: string, value: string): void {
    sessionStorage.setItem(key, value);
  }

  removeKey(key: string): void {
    sessionStorage.removeItem(key);
  }

  clearData(): void {
    sessionStorage.clear();
  }
}
