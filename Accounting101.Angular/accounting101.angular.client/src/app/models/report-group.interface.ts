export interface ReportGroupModel {
  /**
   * The name of the account group
   */
  name: string;

  /**
   * Optional array of account IDs that belong directly to this group
   */
  accounts?: string[];

  /**
   * Optional array of child account groups for hierarchical organization
   */
  children?: ReportGroupModel[];
}
