import { ApprovalMode } from '../approval-policy/approval-policy';

export interface CapabilitiesResponse {
  capabilities: string[];
  roles: string[];
  deploymentAdmin: boolean;
  enabledModules: string[];
  approvalMode: ApprovalMode;
}

export const EMPTY_CAPABILITIES: CapabilitiesResponse = {
  capabilities: [], roles: [], deploymentAdmin: false, enabledModules: [], approvalMode: 'TwoPerson',
};
