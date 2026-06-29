export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5000',
  // DevToken identity (must match a control-DB membership for real data; see plan)
  devUserId: '00000000-0000-0000-0000-000000000001',
  devUserName: 'Dev User',
  devClaims: [{ type: 'role', value: 'Controller' }, { type: 'admin', value: 'true' }] as { type: string; value: string }[],
  // Active client in dev (no per-user "my clients" endpoint yet)
  devClientId: '' as string, // set to the seeded demo client's Guid before demoing
};
