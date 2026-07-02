import { DEV_IDENTITIES } from './dev-identities';

/** Best-effort display name for a member userId (known dev identities → their name; else the id). */
export function memberDisplayName(userId: string): string {
  return DEV_IDENTITIES.find((i) => i.sub === userId)?.name ?? userId;
}
