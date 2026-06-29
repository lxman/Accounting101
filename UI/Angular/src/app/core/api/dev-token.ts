export interface DevClaim { type: string; value: string; }
export interface DevTokenPayload { sub: string; name?: string; claims: DevClaim[]; }

export function encodeDevToken(payload: DevTokenPayload): string {
  const json = JSON.stringify(payload);
  const b64 = btoa(unescape(encodeURIComponent(json)));   // utf8-safe base64
  return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); // base64url, no padding
}
