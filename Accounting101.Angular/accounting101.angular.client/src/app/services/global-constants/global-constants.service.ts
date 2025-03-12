import { Injectable } from '@angular/core';

@Injectable({
  providedIn: 'root'
})
export class GlobalConstantsService {
  readonly baseAppUrl: string = 'https://localhost:7165';
  readonly userIdKey: string = 'key1';
  readonly clientIdKey: string = 'key2';

  constructor() { }
}
