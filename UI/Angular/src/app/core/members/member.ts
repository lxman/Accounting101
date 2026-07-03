export interface Member { userId: string; roles: string[]; capabilities: string[]; grantedSetIds: string[]; setNames: string[]; }
export interface AddMemberRequest { userId: string; roles: string[]; capabilities: string[]; }
export interface SetMemberRequest { roles: string[]; capabilities: string[]; }
export interface RolePreset { role: string; capabilities: string[]; }
export interface CapabilityCatalog { capabilities: string[]; roles: RolePreset[]; }
export interface AssignSetsRequest { setIds: string[]; }
