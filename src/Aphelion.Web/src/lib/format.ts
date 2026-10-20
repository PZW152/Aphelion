import { locale, t } from './i18n/index.svelte';

export function bytes(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < units.length - 1) {
    n /= 1024;
    i++;
  }
  return `${n >= 100 || i === 0 ? Math.round(n) : n.toFixed(1)} ${units[i]}`;
}

export function megabytes(mb: number): string {
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`;
}

/** Download counts the way each language reads them (萬/億 in Chinese, K/M in English). */
export function count(n: number): string {
  if (locale.lang === 'en') {
    if (n >= 1e9) return t('units.y', { n: (n / 1e9).toFixed(1) });
    if (n >= 1e6) return t('units.m', { n: (n / 1e6).toFixed(1) });
    if (n >= 1e3) return t('units.k', { n: (n / 1e3).toFixed(1) });
    return String(n);
  }
  if (n >= 1e8) return t('units.y', { n: (n / 1e8).toFixed(1) });
  if (n >= 1e4) return t('units.m', { n: Math.round(n / 1e4).toLocaleString('zh') });
  return n.toLocaleString('zh');
}

export function relative(iso: string | null | undefined): string {
  if (!iso) return t('time.never');
  const then = new Date(iso).getTime();
  const diff = (Date.now() - then) / 1000;
  if (diff < 60) return t('time.justNow');
  if (diff < 3600) return t('time.minutes', { n: Math.floor(diff / 60) });
  if (diff < 86400) return t('time.hours', { n: Math.floor(diff / 3600) });
  if (diff < 86400 * 14) return t('time.days', { n: Math.floor(diff / 86400) });
  return date(iso);
}

export function date(iso: string | null | undefined): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString(locale.lang, { year: 'numeric', month: 'short', day: 'numeric' });
}

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  return new Date(iso).toLocaleString(locale.lang, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

export function playtime(seconds: number): string {
  if (seconds < 3600) return t('time.minutesTotal', { n: Math.round(seconds / 60) });
  return t('time.hoursTotal', { n: Math.round(seconds / 3600) });
}

export function clock(ms: number): string {
  const s = Math.max(0, ms / 1000);
  const m = Math.floor(s / 60);
  return `${String(m).padStart(2, '0')}:${(s - m * 60).toFixed(1).padStart(4, '0')}`;
}

/** TimeSpan "hh:mm:ss.fffffff" → readable duration. */
export function timespan(text: string): string {
  const m = /^(?:(\d+)\.)?(\d+):(\d+):(\d+)/.exec(text);
  if (!m) return text;
  const h = Number(m[1] ?? 0) * 24 + Number(m[2]);
  return h > 0 ? `${h}:${m[3]}:${m[4]}` : `${Number(m[3])}:${m[4]}`;
}
