<script lang="ts">
  import { untrack } from 'svelte';
  import { app } from '../lib/app.svelte';
  import { call, on } from '../lib/rpc';
  import { describeError, t } from '../lib/i18n/index.svelte';
  import { bytes } from '../lib/format';
  import type { ModePlan } from '../lib/types';
  import Dialog from '../components/Dialog.svelte';
  import Icon from '../components/Icon.svelte';
  import { rise } from '../lib/motion';

  let { open = $bindable(false), to }: { open?: boolean; to: 'standard' | 'portable' } = $props();

  let plan = $state<ModePlan | null>(null);
  let folder = $state<string | null>(null);
  let chosen = $state<Record<string, boolean>>({ minecraft: true, runtime: true });
  let phase = $state<'plan' | 'moving' | 'restarting'>('plan');
  let progress = $state<{ item: string; done: number; total: number } | null>(null);
  let error = $state<string | null>(null);

  const moving = $derived(phase !== 'plan');
  const selected = $derived(plan?.items.filter((i) => !i.conflict && (i.required || chosen[i.key])) ?? []);
  const total = $derived(selected.reduce((n, i) => n + i.bytes, 0));
  const fraction = $derived(progress && progress.total > 0 ? Math.min(1, progress.done / progress.total) : null);

  async function load() {
    error = null;
    try {
      plan = await call<ModePlan>('app.modePlan', { to, folder });
    } catch (e) {
      plan = null;
      error = describeError(e);
    }
  }

  // A fresh plan every time the dialog opens: sizes and conflicts change while the launcher runs.
  $effect(() => {
    if (!open) return;
    untrack(() => {
      phase = 'plan';
      progress = null;
      folder = null;
      load();
    });
    return on('mode.progress', (p: { item: string; done: number; total: number }) => (progress = p));
  });

  async function pick() {
    const path = await call<string | null>('dialog.pickFolder').catch(() => null);
    if (!path) return;
    folder = path;
    await load();
  }

  async function beside() {
    folder = null;
    await load();
  }

  async function run() {
    if (!plan) return;
    phase = 'moving';
    error = null;
    try {
      await call('app.switchMode', { to, folder, items: Object.keys(chosen).filter((k) => chosen[k]) });
      phase = 'restarting';
    } catch (e) {
      phase = 'plan';
      error = describeError(e);
      await load();
    }
  }
</script>

<Dialog bind:open title={t('mode.title.' + to)} width={620} locked={moving}>
  <p class="lead">{t('mode.lead.' + to)}</p>

  {#if to === 'portable' && plan}
    <div class="where">
      <span class="t-label">{t('mode.where')}</span>
      <div class="choices">
        <button class="choice" aria-pressed={!folder} disabled={moving || plan.installed} onclick={beside}>
          <strong>{t('mode.where.beside')}</strong>
          <span>{plan.installed ? t('mode.where.installed') : app.info?.executable?.replace(/\\[^\\]+$/, '')}</span>
        </button>
        <button class="choice" aria-pressed={!!folder} disabled={moving} onclick={pick}>
          <strong>{t('mode.where.other')}{folder ? '' : ' …'}</strong>
          <span>{folder ? plan.programFolder : t('mode.where.other.hint')}</span>
        </button>
      </div>
    </div>
  {/if}

  {#if plan}
    <div class="route" class:live={phase === 'moving'} aria-label={t('mode.route')}>
      <span class="node"><span class="t-legend">FROM</span><span class="t-mono">{app.info?.dataRoot}</span></span>
      <span class="wire" aria-hidden="true"><span class="packet"></span></span>
      <span class="node"><span class="t-legend">TO</span><span class="t-mono">{plan.dataRoot}</span></span>
    </div>

    <span class="t-label">{t('mode.items')}</span>
    <ul class="screen items">
      {#each plan.items as item (item.key)}
        <li class:off={item.conflict || (!item.required && !chosen[item.key])}>
          <span class="name">{t('mode.item.' + item.key)}</span>
          <span class="size tnum">{bytes(item.bytes)}</span>
          {#if item.required}
            <span class="chip">{t('mode.item.required')}</span>
          {:else if item.conflict}
            <span class="chip warn">—</span>
          {:else}
            <button class="switch" role="switch" aria-checked={chosen[item.key]} aria-label={t('mode.item.' + item.key)} disabled={moving}
              onclick={() => (chosen[item.key] = !chosen[item.key])}></button>
          {/if}
          {#if item.conflict}
            <span class="note warn">{t('mode.item.conflict')}</span>
          {:else if !item.required && !chosen[item.key]}
            <span class="note" in:rise={{ y: -4 }}>{t('mode.item.' + item.key + '.left')}</span>
          {/if}
        </li>
      {/each}
    </ul>
    <p class="hint">{plan.sameVolume ? t('mode.sameVolume') : t('mode.crossVolume', { v: bytes(total) })}</p>
  {/if}

  {#if phase === 'moving'}
    <div class="progress" in:rise>
      <span class="t-label tnum">{progress?.item ? t('mode.moving', { item: t('mode.item.' + progress.item) }) : t('common.loading')}{fraction != null && !plan?.sameVolume ? ` · ${bytes(progress!.done)} / ${bytes(progress!.total)}` : ''}</span>
      <span class="bar live" class:indeterminate={fraction == null}><span style:width="{(fraction ?? 0) * 100}%"></span></span>
    </div>
  {:else if phase === 'restarting'}
    <p class="done" in:rise><Icon name="check" size={16} />{t('mode.restarting')}</p>
  {/if}

  {#if plan?.blocker && phase === 'plan'}<p class="blocker" in:rise><Icon name="alert" size={16} />{t('mode.blocker.' + plan.blocker)}</p>{/if}
  {#if error}<p class="blocker" in:rise><Icon name="alert" size={16} />{error}</p>{/if}

  {#snippet footer()}
    <button class="btn" disabled={moving} onclick={() => (open = false)}>{t('common.cancel')}</button>
    <button class="btn primary" disabled={!plan || !!plan.blocker || moving} onclick={run}><Icon name="refresh" size={14} />{t('mode.confirm')}</button>
  {/snippet}
</Dialog>

<style>
  .lead { font-size: 14px; line-height: 1.65; color: var(--ink-2); }
  .where { display: flex; flex-direction: column; gap: 8px; }
  .choices { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
  .choice {
    all: unset; box-sizing: border-box; cursor: pointer; display: flex; flex-direction: column; gap: 4px; padding: 12px 14px;
    border: 1px solid var(--rule); background: var(--panel-raised); font-size: 12px; color: var(--muted); line-height: 1.5; min-width: 0;
    transition: border-color var(--t-fast), background var(--t-fast);
  }
  .choice span { overflow-wrap: anywhere; }
  .choice strong { font-size: 14px; color: var(--ink); }
  .choice:hover:not(:disabled) { border-color: var(--rule-strong); }
  .choice[aria-pressed='true'] { border-color: var(--accent); box-shadow: inset 3px 0 0 var(--accent); }
  .choice:disabled { cursor: not-allowed; opacity: 0.55; }
  .choice:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

  /* The route: two terminals and a wire; while moving, a packet runs along it. */
  .route { display: grid; grid-template-columns: minmax(0, 1fr) 72px minmax(0, 1fr); align-items: center; gap: 10px; padding: 14px; background: var(--screen); border: 1px solid var(--rule); }
  .node { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .node .t-mono { font-size: 12px; overflow-wrap: anywhere; color: var(--ink-2); }
  .wire { position: relative; height: 2px; background: repeating-linear-gradient(90deg, var(--rule-strong) 0 6px, transparent 6px 10px); }
  .wire::after { content: ''; position: absolute; right: -2px; top: -4px; border: 5px solid transparent; border-left-color: var(--rule-strong); border-right: 0; }
  .packet { position: absolute; top: -3px; left: 0; width: 8px; height: 8px; background: var(--accent); opacity: 0; }
  .live .wire { animation: wire 600ms linear infinite; background-image: repeating-linear-gradient(90deg, var(--accent) 0 6px, transparent 6px 10px); }
  .live .wire::after { border-left-color: var(--accent); }
  .live .packet { animation: packet 1.1s var(--ease-in-out) infinite; }
  @keyframes wire { to { background-position: 10px 0; } }
  @keyframes packet { 0% { left: 0; opacity: 0; } 15%, 85% { opacity: 1; } 100% { left: calc(100% - 8px); opacity: 0; } }

  .items { display: flex; flex-direction: column; margin-top: -6px; }
  .items li { display: grid; grid-template-columns: minmax(0, 1fr) auto 56px; gap: 6px 14px; align-items: center; padding: 11px 14px; border-bottom: 1px solid var(--rule-faint); }
  .items li:last-child { border-bottom: 0; }
  .items .name { font-size: 14px; font-weight: 600; transition: color var(--t-base); }
  .items .off .name, .items .off .size { color: var(--muted); }
  .size { font-size: 12px; color: var(--ink-3); transition: color var(--t-base); }
  .items .chip, .items .switch { justify-self: end; }
  .note { grid-column: 1 / -1; font-size: 12px; color: var(--muted); line-height: 1.5; }
  .warn { color: var(--caution); }
  .hint { font-size: 12px; color: var(--muted); line-height: 1.55; }
  .progress { display: flex; flex-direction: column; gap: 8px; }
  .progress .bar { height: 6px; }
  .done { display: flex; gap: 8px; align-items: center; color: var(--nominal); font-weight: 600; }
  .blocker { display: flex; gap: 8px; align-items: flex-start; color: var(--caution); font-size: 13px; line-height: 1.55; padding: 10px 12px; border: 1px solid color-mix(in oklab, var(--caution) 40%, var(--rule)); }
  @media (max-width: 720px) { .choices { grid-template-columns: 1fr; } }
</style>
