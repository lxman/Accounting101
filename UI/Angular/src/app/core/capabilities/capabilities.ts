export interface CapabilitiesResponse {
  capabilities: string[];
  roles: string[];
  deploymentAdmin: boolean;
  enabledModules: string[];
}

export const EMPTY_CAPABILITIES: CapabilitiesResponse = {
  capabilities: [], roles: [], deploymentAdmin: false, enabledModules: [],
};
