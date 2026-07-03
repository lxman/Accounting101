export interface CapabilitySet {
  id: string;
  name: string;
  description?: string;
  capabilities: string[];
  builtin: boolean;
  affectedMemberCount: number;
}

export interface CreateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
}

export interface UpdateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
}
