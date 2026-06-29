export interface ClaimResponse { type: string; value: string; }
export interface ActorResponse { userId: string; name: string | null; claims: ClaimResponse[]; }
export interface AuditRecordResponse {
  sequence: number; action: string; entryId: string | null; entryVersion: number;
  at: string; reason: string | null; actor: ActorResponse;
}
