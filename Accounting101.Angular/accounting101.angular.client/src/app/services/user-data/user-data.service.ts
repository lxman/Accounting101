import { Injectable } from '@angular/core';
import * as encryption from '../../services/encryption/encryption.service';

@Injectable({
  providedIn: 'root'
})

export class UserDataService {

  get(key: string): string {
    return encryption.decrypt(sessionStorage.getItem(key) ?? '');
  }

  set(key: string, value: string): void {
    sessionStorage.setItem(key, encryption.encrypt(value));
  }

  removeKey(key: string): void {
    sessionStorage.removeItem(key);
  }

  clearData(): void {
    sessionStorage.clear();
  }
}
