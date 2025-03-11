import { Injectable } from '@angular/core';

@Injectable({
  providedIn: 'root'
})

export class UserDataService {
  private userData: Map<string, string> = new Map<string, string>();

  get(key: string): string {
    return this.userData.get(key) ?? '';
  }

  set(key: string, value: string): void {
    this.userData.set(key, value);
  }
}
