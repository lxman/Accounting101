export interface CapabilitySet {
  id: string;
  name: string;
  description?: string;
  capabilities: string[];
  builtin: boolean;
  affectedMemberCount: number;
  restricted: boolean;
}

export interface CreateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
  restricted?: boolean;
}

export interface UpdateCapabilitySetRequest {
  name: string;
  description?: string;
  capabilities: string[];
  restricted?: boolean;
}
