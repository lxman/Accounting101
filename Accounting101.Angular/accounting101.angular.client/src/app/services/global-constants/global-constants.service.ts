import { Injectable } from '@angular/core';

@Injectable({
  providedIn: 'root'
})
export class GlobalConstantsService {
  readonly baseServerUrl: string = 'https://localhost:7165';
  readonly userIdKey: string = 'key1';
  readonly rolesKey: string = 'key2';
  readonly clientIdKey: string = 'key3';

  constructor() { }
}
