import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../api/environment';
import { CapabilitySet, CreateCapabilitySetRequest, UpdateCapabilitySetRequest } from './capability-set';

/** Deployment-scoped capability-set CRUD (no client context — the route is /capability-sets). */
@Injectable({ providedIn: 'root' })
export class CapabilitySetService {
  private readonly http = inject(HttpClient);
  private base(path = ''): string { return `${environment.apiBaseUrl}/capability-sets${path}`; }

  list(): Observable<CapabilitySet[]> { return this.http.get<CapabilitySet[]>(this.base()); }
  create(req: CreateCapabilitySetRequest): Observable<CapabilitySet> { return this.http.post<CapabilitySet>(this.base(), req); }
  update(id: string, req: UpdateCapabilitySetRequest): Observable<CapabilitySet> { return this.http.put<CapabilitySet>(this.base(`/${id}`), req); }
  remove(id: string): Observable<void> { return this.http.delete<void>(this.base(`/${id}`)); }
}
