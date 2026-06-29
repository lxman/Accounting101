export interface DevIdentityConfig { sub: string; name: string; claims: { type: string; value: string }[]; }

export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5000',
  // Two dev identities so maker-checker (author ≠ approver) is exercisable in one browser.
  // Both must map to a control-DB membership for the demo client to get real data (not a build/test dependency).
  devClerk: {
    sub: '00000000-0000-0000-0000-000000000001',
    name: 'Dev Clerk',
    claims: [{ type: 'role', value: 'Controller' }],
  } as DevIdentityConfig,
  devApprover: {
    sub: '00000000-0000-0000-0000-000000000002',
    name: 'Dev Approver',
    claims: [{ type: 'role', value: 'Approver' }, { type: 'admin', value: 'true' }],
  } as DevIdentityConfig,
  devClientId: '' as string, // set to the seeded demo client's Guid before demoing
};
