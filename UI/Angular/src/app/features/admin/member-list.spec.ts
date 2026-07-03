import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MemberList } from './member-list';
import { ClientContextService } from '../../core/client/client-context.service';
import { provideCapabilities } from '../../core/capabilities/capability.testing';

function setup() {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideCapabilities('admin.users')],
  });
  TestBed.inject(ClientContextService).select('C1');
  return TestBed.inject(HttpTestingController);
}

describe('MemberList', () => {
  it('renders member rows with name, roles, and capability count', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MemberList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/members').flush([
      { userId: '00000000-0000-0000-0000-000000000004', roles: ['ArClerk'], capabilities: ['ar.read', 'ar.write'] },
    ]);
    f.detectChanges();
    const text = f.nativeElement.textContent;
    expect(text).toContain('Dev AR Clerk');
    expect(text).toContain('ArClerk');
    expect(text).toContain('2');
  });

  it('shows the empty state', () => {
    const ctrl = setup();
    const f = TestBed.createComponent(MemberList); f.detectChanges();
    ctrl.expectOne('http://localhost:5000/clients/C1/members').flush([]);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('No members yet');
  });
});
