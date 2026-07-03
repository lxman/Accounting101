import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { EMPTY, Observable } from 'rxjs';
import { environment } from '../api/environment';
import { ClientContextService } from '../client/client-context.service';
import { Member, AddMemberRequest, SetMemberRequest, CapabilityCatalog, AssignSetsRequest } from './member';

@Injectable({ providedIn: 'root' })
export class MemberService {
  private readonly http = inject(HttpClient);
  private readonly client = inject(ClientContextService);

  private base(path = ''): string { return `${environment.apiBaseUrl}/clients/${this.client.clientId()}/members${path}`; }

  list(): Observable<Member[]> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.get<Member[]>(this.base());
  }
  add(req: AddMemberRequest): Observable<Member> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.post<Member>(this.base(), req);
  }
  set(userId: string, req: SetMemberRequest): Observable<Member> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<Member>(this.base(`/${userId}`), req);
  }
  remove(userId: string): Observable<void> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.delete<void>(this.base(`/${userId}`));
  }
  catalog(): Observable<CapabilityCatalog> {
    return this.http.get<CapabilityCatalog>(`${environment.apiBaseUrl}/capabilities/catalog`);
  }
  assignSets(userId: string, req: AssignSetsRequest): Observable<Member> {
    if (!this.client.clientId()) return EMPTY;
    return this.http.put<Member>(this.base(`/${userId}/sets`), req);
  }
}
