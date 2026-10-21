<script lang="ts">
  import { app, fail, updateSettings } from '../lib/app.svelte';
  import { call, on } from '../lib/rpc';
  import { describeError, errorText, locale, t } from '../lib/i18n/index.svelte';
  import { bytes, relative } from '../lib/format';
  import type { UpdateState } from '../lib/types';
  import Icon from '../components/Icon.svelte';
  import Markdown from '../components/Markdown.svelte';
  import { rise } from '../lib/motion';
  import { onMount } from 'svelte';

  let phase = $state<'idle' | 'downloading' | 'restarting'>('idle');
  let progress = $state<{ done: number; total: number } | null>(null);
  let error = $state<string | null>(null);

  const u = $derived(app.update);
  const notes = $derived(u?.available ? (u.available.notes[locale.lang] ?? u.available.notes['zh-Hant'] ?? u.available.notes['en'] ?? '') : '');

  onMount(() => on('update.progress', (p: { done: number; total: number }) => (progress = p)));

  async function check() {
    error = null;
    try {
      app.update = await call<UpdateState>('update.check');
    } catch (e) {
      fail(e);
    }
  }

  async function install() {
    error = null;
    phase = 'downloading';
    try {
      await call('update.install');
      phase = 'restarting';
    } catch (e) {
      phase = 'idle';
      error = describeError(e);
    }
  }
</script>

<div class="update" class:has={!!u?.available}>
  <div class="status">
    <span class="lamp {u?.checking ? 'caution pulse' : u?.available ? 'accent' : u?.error ? 'warning' : 'ok'}"></span>
    <div class="lines">
      <strong>
        {#if u?.checking}{t('update.checking')}
        {:else if u?.available}{t('update.available', { v: u.available.version })}
        {:else if u?.error}{errorText(u.error, null)}
        {:else}{t('update.latest')}{/if}
      </strong>
      <span class="muted">{t('update.current', { v: u?.current ?? app.info?.version ?? '' })}{u?.lastChecked ? ' · ' + t('update.checked', { t: relative(u.lastChecked) }) : ''}</span>
    </div>
    <div class="actions">
      {#if u?.available && phase === 'idle'}
        <button class="btn primary sm" onclick={install}><Icon name="download" size={14} />{t('update.install')} · {bytes(u.available.size)}</button>
      {/if}
      <button class="btn sm" disabled={u?.checking || phase !== 'idle'} onclick={check}><Icon name="refresh" size={14} />{t('update.check')}</button>
    </div>
  </div>

  {#if phase === 'downloading'}
    <div class="progress" in:rise>
      <span class="t-label tnum">{progress ? t('update.downloading', { done: bytes(progress.done), total: bytes(progress.total) }) : t('common.loading')}</span>
      <span class="bar live" class:indeterminate={!progress?.total}><span style:width="{progress?.total ? (progress.done / progress.total) * 100 : 0}%"></span></span>
    </div>
  {:else if phase === 'restarting'}
    <p class="done" in:rise><Icon name="check" size={16} />{t('update.restarting')}</p>
  {/if}
  {#if error}<p class="error" in:rise><Icon name="alert" size={16} />{error}</p>{/if}

  {#if u?.available && notes}
    <div class="notes screen" in:rise>
      <span class="t-label">{t('update.notes')} · {u.available.version}</span>
      <Markdown source={notes} />
      {#if u.available.page}<button class="link" onclick={() => call('app.openUrl', { url: u!.available!.page }).catch(fail)}>{t('update.page')} <Icon name="external" size={12} /></button>{/if}
    </div>
  {/if}

  <div class="auto">
    <button class="switch" role="switch" aria-checked={app.settings?.checkForUpdates ?? true} aria-label={t('update.auto')}
      onclick={() => updateSettings({ checkForUpdates: !(app.settings?.checkForUpdates ?? true) })}></button>
    <div><span>{t('update.auto')}</span><p class="hint">{t('update.auto.hint')}</p></div>
  </div>
  <p class="hint">{t('update.verified')}</p>
</div>

<style>
  .update { display: flex; flex-direction: column; gap: 12px; }
  .status { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; gap: 12px; align-items: center; padding: 14px; border: 1px solid var(--rule); background: var(--panel-raised); transition: border-color var(--t-base); }
  .has .status { border-color: var(--accent); box-shadow: inset 3px 0 0 var(--accent); }
  .status .lamp { width: 8px; height: 8px; }
  .lines { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
  .lines strong { font-size: 15px; }
  .lines .muted { font-size: 12px; }
  .actions { display: flex; gap: 8px; flex-wrap: wrap; justify-content: flex-end; }
  .progress { display: flex; flex-direction: column; gap: 8px; }
  .progress .bar { height: 6px; }
  .done { display: flex; gap: 8px; align-items: center; color: var(--nominal); font-weight: 600; }
  .error { display: flex; gap: 8px; align-items: flex-start; color: var(--warning); font-size: 13px; }
  .notes { padding: 14px; display: flex; flex-direction: column; gap: 8px; font-size: 13px; }
  .link { all: unset; cursor: pointer; color: var(--accent-ink); font-size: 13px; display: inline-flex; gap: 4px; align-items: center; }
  .link:focus-visible { outline: 2px solid var(--accent); }
  .auto { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 12px; align-items: start; font-size: 14px; }
  .auto .switch { margin-top: 2px; }
  .hint { font-size: 12px; color: var(--muted); line-height: 1.55; }
</style>
