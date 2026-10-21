<script lang="ts">
  import { app, fail } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { t } from '../lib/i18n/index.svelte';
  import { bytes } from '../lib/format';
  import type { HostCheck } from '../lib/types';
  import Icon from '../components/Icon.svelte';
  import { rise, stagger } from '../lib/motion';

  let running = $state(false);
  const rows = $derived(app.network);
  const failed = $derived(rows?.filter((r) => !r.ok).length ?? 0);
  // Bars are scaled to the fastest host measured in this run.
  const fastest = $derived(Math.max(1, ...(rows ?? []).map((r) => r.bytesPerSecond ?? 0)));

  async function run() {
    running = true;
    try {
      app.network = await call<HostCheck[]>('network.check');
    } catch (e) {
      fail(e);
    } finally {
      running = false;
    }
  }

  const ms = (v: number | null) => (v == null ? '—' : `${Math.round(v)} ms`);
</script>

<div class="check">
  <div class="head">
    <div>
      <h3 class="t-label">{t('net.check')}</h3>
      <p class="hint">{t('net.check.hint')}</p>
    </div>
    <button class="btn sm" disabled={running} onclick={run}>
      <span class:spin={running}><Icon name="signal" size={14} /></span>{running ? t('net.check.running') : rows ? t('net.check.again') : t('net.check.run')}
    </button>
  </div>

  {#if rows}
    <table class="screen" class:stale={running}>
      <thead><tr><th>{t('net.col.host')}</th><th>{t('net.col.resolve')}</th><th>{t('net.col.firstByte')}</th><th>{t('net.col.speed')}</th></tr></thead>
      <tbody>
        {#each rows as r, i (r.key)}
          <tr in:rise={{ delay: stagger(i, 30), y: 4 }}>
            <td>
              <div class="who">
                <span class="lamp {r.ok ? 'ok' : 'warning'}"></span>
                <span class="name">{t('net.host.' + r.key)}</span>
                <span class="t-mono host">{r.host}</span>
              </div>
            </td>
            <td class="tnum">{ms(r.resolveMs)}</td>
            <td class="tnum">{r.ok ? ms(r.firstByteMs) : ''}</td>
            <td><div class="speed">
              {#if !r.ok}
                <span class="err">{r.error?.startsWith('http') ? r.error.toUpperCase() : t('net.error.' + (r.error ?? 'connect'))}</span>
              {:else if r.bytesPerSecond}
                <span class="meter"><span style:width="{(r.bytesPerSecond / fastest) * 100}%"></span></span>
                <span class="tnum">{bytes(r.bytesPerSecond)}/s</span>
              {:else}
                <span class="muted">—</span>
              {/if}
            </div></td>
          </tr>
        {/each}
      </tbody>
    </table>
    <p class="summary" class:bad={failed > 0} in:rise>
      {failed === 0 ? t('net.summary.ok') : failed === rows.length ? t('net.summary.down') : t('net.summary.partial', { n: failed })}
    </p>
  {/if}
</div>

<style>
  .check { display: flex; flex-direction: column; gap: 10px; }
  .head { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; }
  .head .hint { margin-top: 4px; }
  .spin { display: inline-grid; animation: pulse-signal 900ms steps(4, end) infinite; }
  @keyframes pulse-signal { 50% { opacity: 0.35; } }
  table { width: 100%; border-collapse: collapse; font-size: 13px; transition: opacity var(--t-base); }
  table.stale { opacity: 0.55; }
  th { text-align: left; font-size: 11px; font-weight: 600; color: var(--muted); text-transform: uppercase; letter-spacing: 0.08em; padding: 10px 14px; border-bottom: 1px solid var(--rule); }
  td { padding: 9px 14px; border-bottom: 1px solid var(--rule-faint); vertical-align: middle; white-space: nowrap; }
  tr:last-child td { border-bottom: 0; }
  .who { display: flex; align-items: center; gap: 8px; min-width: 0; }
  .hint { font-size: 12px; color: var(--muted); line-height: 1.55; }
  .lamp { width: 7px; height: 7px; flex: none; }
  .name { font-weight: 600; }
  .host { font-size: 11px; color: var(--faint); overflow: hidden; text-overflow: ellipsis; }
  .speed { display: flex; align-items: center; gap: 10px; min-width: 180px; }
  .meter { flex: 1; height: 4px; background: var(--rule); position: relative; min-width: 60px; }
  .meter span { position: absolute; inset: 0 auto 0 0; background: var(--nominal); transform-origin: left; animation: meter-in 520ms var(--ease) both; }
  @keyframes meter-in { from { transform: scaleX(0); } }
  .err { color: var(--warning); font-weight: 600; }
  .summary { font-size: 13px; color: var(--nominal); }
  .summary.bad { color: var(--caution); }
  @media (max-width: 1040px) { .host { display: none; } }
</style>
