import { Injectable } from '@angular/core';
import { environment } from '../../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class GlobalConstantsService {
  readonly baseServerUrl: string = environment.apiUrl;
  readonly userIdKey: string = 'key1';
  readonly rolesKey: string = 'key2';
  readonly clientIdKey: string = 'key3';
  readonly accountIdKey: string = 'key4';

  // Values are in seconds
  readonly totalTimeout: number = 30 * 60;
  readonly idleTimeout: number = this.totalTimeout * 0.75;
  readonly loginTimeout: number = this.totalTimeout * 0.25;
}
