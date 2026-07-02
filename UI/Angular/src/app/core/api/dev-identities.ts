import { DevIdentity } from './dev-identity.service';

/** Fixed dev identities for the "Acting as" switcher. Subs are fixed non-secret GUIDs; each gets a
 * membership seeded in .localdev so /me/capabilities resolves distinct capability sets. Role claims
 * are decorative (authority comes from the membership); admin=true drives the deployment-admin flag. */
export const DEV_IDENTITIES: DevIdentity[] = [
  { sub: '00000000-0000-0000-0000-000000000001', name: 'Dev Controller', claims: [{ type: 'role', value: 'Controller' }] },
  { sub: '00000000-0000-0000-0000-000000000002', name: 'Dev Approver', claims: [{ type: 'role', value: 'Approver' }, { type: 'admin', value: 'true' }] },
  { sub: '00000000-0000-0000-0000-000000000003', name: 'Dev Auditor', claims: [{ type: 'role', value: 'Auditor' }] },
  { sub: '00000000-0000-0000-0000-000000000004', name: 'Dev AR Clerk', claims: [{ type: 'role', value: 'ArClerk' }] },
  { sub: '00000000-0000-0000-0000-000000000005', name: 'Dev Admin', claims: [{ type: 'role', value: 'Admin' }, { type: 'admin', value: 'true' }] },
];
