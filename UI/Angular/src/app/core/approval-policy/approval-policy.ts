export type ApprovalMode = 'TwoPerson' | 'SelfApprove' | 'AutoApprove';

export interface ApprovalPolicy {
  mode: ApprovalMode;
}
