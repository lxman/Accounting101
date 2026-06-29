import { formatDate } from '@angular/common';
import { FormatProfile } from './format-profile';

export const formatProfileDate = (date: string | number | Date, profile: FormatProfile, locale = 'en-US'): string =>
  formatDate(date, profile.dateFormat, locale);
