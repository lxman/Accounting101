import { encodeDevToken, DevTokenPayload } from './dev-token';

describe('encodeDevToken', () => {
  it('produces a base64url(JSON) string that round-trips to the payload', () => {
    const payload: DevTokenPayload = { sub: 'abc', name: 'Dev', claims: [{ type: 'role', value: 'Controller' }] };
    const enc = encodeDevToken(payload);
    expect(enc).not.toContain('+'); expect(enc).not.toContain('/'); expect(enc).not.toContain('=');
    const json = JSON.parse(decodeURIComponent(escape(atob(enc.replace(/-/g, '+').replace(/_/g, '/')))));
    expect(json).toEqual(payload);
  });
});
