export interface CapabilitiesResponse {
  capabilities: string[];
  roles: string[];
  deploymentAdmin: boolean;
}

export const EMPTY_CAPABILITIES: CapabilitiesResponse = { capabilities: [], roles: [], deploymentAdmin: false };
