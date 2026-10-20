import zhHant from './zh-Hant';
import zhHans from './zh-Hans';
import en from './en';
import { RpcError } from '../rpc';

export type Lang = 'zh-Hant' | 'zh-Hans' | 'en';
export type Key = keyof typeof zhHant;

const dictionaries: Record<Lang, Partial<Record<Key, string>>> = { 'zh-Hant': zhHant, 'zh-Hans': zhHans, en };

export const locale = $state<{ lang: Lang }>({ lang: 'zh-Hant' });

/** Maps a setting ("auto" or a language) and the system culture to a supported language. */
export function resolveLang(setting: string, culture: string): Lang {
  if (setting === 'zh-Hant' || setting === 'zh-Hans' || setting === 'en') return setting;
  const c = culture.toLowerCase();
  if (c.startsWith('zh')) return /hant|tw|hk|mo/.test(c) ? 'zh-Hant' : 'zh-Hans';
  return 'en';
}

export function setLang(lang: Lang) {
  locale.lang = lang;
  document.documentElement.lang = lang;
}

export function t(key: Key | string, params?: Record<string, string | number | null | undefined>): string {
  const dict = dictionaries[locale.lang];
  let text = (dict[key as Key] ?? zhHant[key as Key] ?? key) as string;
  if (params) {
    for (const [k, v] of Object.entries(params)) text = text.split(`{${k}}`).join(v == null ? '' : String(v));
  }
  return text;
}

export function has(key: string): boolean {
  return key in zhHant;
}

/** A user-facing sentence for an error from the host (stable code → translation, message as fallback). */
export function describeError(e: unknown): string {
  if (e instanceof RpcError) return errorText(e.code, e.message, e.args);
  return t('error.generic', { message: e instanceof Error ? e.message : String(e) });
}

export function errorText(code: string | null, message: string | null, args?: Record<string, string> | null): string {
  const key = `error.${code}`;
  if (code && has(key)) return t(key, { message: message ?? '', ...(args ?? {}) });
  return t('error.generic', { message: message ?? code ?? '' });
}
