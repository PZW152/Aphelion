<script lang="ts">
  import { app, go, launch, launchTask, liveSessions, selectInstance, selectedInstance, fail } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { t } from '../lib/i18n/index.svelte';
  import { clock, megabytes, playtime, relative } from '../lib/format';
  import ViewHeader from '../components/ViewHeader.svelte';
  import Annunciators from '../components/Annunciators.svelte';
  import OrbitMap from '../components/OrbitMap.svelte';
  import Icon from '../components/Icon.svelte';
  import { rise, ticker } from '../lib/motion';

  const stages = ['launch.verify', 'launch.java', 'launch.auth', 'launch.natives', 'launch.arguments', 'launch.ignition'];

  const instance = $derived(selectedInstance());
  const task = $derived(instance ? launchTask(instance.id) : null);
  const session = $derived(instance ? liveSessions().find((s) => s.instanceId === instance.id) : undefined);
  const stageIndex = $derived(task?.stage ? stages.indexOf(task.stage) : session ? stages.length : -1);

  let now = $state(Date.now());
  $effect(() => {
    if (!task) return;
    const timer = setInterval(() => (now = Date.now()), 100);
    return () => clearInterval(timer);
  });

  const javaNeeded = $derived(
    instance?.gameVersion && /^1\.(\d+)/.test(instance.gameVersion)
      ? (() => {
          const minor = Number(/^1\.(\d+)(?:\.(\d+))?/.exec(instance.gameVersion!)![1]);
          const patch = Number(/^1\.\d+\.(\d+)/.exec(instance.gameVersion!)?.[1] ?? 0);
          return minor >= 21 || (minor === 20 && patch >= 5) ? 21 : minor >= 18 ? 17 : minor === 17 ? 16 : 8;
        })()
      : 21,
  );

  const memoryText = $derived.by(() => {
    if (!instance || !app.settings) return '';
    const m = instance.overrides.memory ?? app.settings.game.memory;
    return m.mode === 'fixed' ? t('launch.memory.fixed', { v: megabytes(m.fixedMb) }) : t('launch.memory.auto');
  });

  function onKey(e: KeyboardEvent) {
    if (e.key !== 'Enter' || e.ctrlKey || e.altKey) return;
    const tag = (e.target as HTMLElement).tagName;
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'BUTTON' || tag === 'SELECT' || document.querySelector('dialog[open]')) return;
    if (instance && !task && !session) launch(instance);
  }

  async function abort() {
    if (task) await call('tasks.cancel', { id: task.id }).catch(fail);
  }
</script>

<svelte:window onkeydown={onKey} />

<ViewHeader title={t('launch.title')} subtitle={app.settings?.activeGameRoot ? t('launch.gameRoot', { path: app.settings.activeGameRoot }) : undefined}>
  <Annunciators />
</ViewHeader>

{#if app.instancesLoaded && app.instances.length === 0}
  <section class="empty-state">
    <div class="empty">
      <h2 class="t-identity" style="font-size: 40px; color: var(--ink)">{t('launch.empty.title')}</h2>
      <p>{t('launch.empty.body')}</p>
      <button class="btn primary lg" onclick={() => (app.createOpen = true)}><Icon name="plus" />{t('launch.empty.cta')}</button>
    </div>
  </section>
{:else if instance}
  <section class="deck">
    <OrbitMap instances={app.instances} selectedId={instance.id} onselect={(id) => selectInstance(id)} />

    <div class="readout">
      <div class="identity">
        <span class="t-label">{t('launch.selected')}</span>
        <!-- The identity plate flips to the new instance; old and new share one grid cell so the layout never jumps. -->
        <div class="plate">
          {#key instance.id}
            <h2 class="t-identity name" title={instance.name} in:ticker={{ delay: 60 }} out:ticker={{ direction: -1, duration: 140 }}>{instance.name}</h2>
          {/key}
        </div>
        {#key instance.id}
        <div class="chips" in:rise={{ delay: 120, y: 4 }}>
          <span class="chip">Minecraft {instance.gameVersion ?? '?'}</span>
          <span class="chip">{t('loader.' + instance.loader.kind)}{instance.loader.version ? ' ' + instance.loader.version : ''}</span>
          <span class="chip">{instance.isolated ? t('launch.isolated') : t('launch.shared')}</span>
        </div>
        {/key}
      </div>

      {#if instance.error}
        <p class="broken"><Icon name="alert" />{t('launch.broken', { error: instance.error })}</p>
      {/if}

      {#key instance.id}
      <dl class="telemetry readings">
        <dt>{t('launch.java')}</dt>
        <dd>{instance.overrides.javaPath ?? app.settings?.game.javaPath ?? t('launch.java.auto', { n: javaNeeded })}</dd>
        <dt>{t('launch.memory')}</dt>
        <dd>{memoryText}</dd>
        {#if instance.loader.kind !== 'vanilla'}
          <dt>{t('launch.mods')}</dt>
          <dd>{t('launch.mods.count', { n: instance.modCount })}{instance.disabledModCount ? ' · ' + t('launch.mods.disabled', { n: instance.disabledModCount }) : ''}</dd>
        {/if}
        <dt>{t('launch.lastPlayed')}</dt>
        <dd>{relative(instance.lastPlayed)}{instance.playSeconds > 0 ? ' · ' + playtime(instance.playSeconds) : ''}</dd>
      </dl>
      {/key}

      <div class="actions">
        <button class="btn" onclick={() => call('instances.openFolder', { id: instance.id }).catch(fail)}><Icon name="folder" />{t('common.openFolder')}</button>
        <button class="btn" onclick={() => { app.focusInstance = instance.id; app.instanceTab = 'settings'; go('instances'); }}><Icon name="gear" />{t('launch.instanceSettings')}</button>
      </div>
    </div>
  </section>

  <footer class="ignition">
    <ol class="sequence" aria-label={t('launch.sequence')}>
      {#each stages as s, i (s)}
        {@const state = i < stageIndex ? 'done' : i === stageIndex ? 'active' : 'idle'}
        <li class={state} style:--i={i}>
          <span class="track">
            {#if state === 'done'}<span class="done-fill"></span>{/if}
            {#if state === 'active' && task}<span class="fill" style:width="{Math.max(8, (task.fraction ?? 0.35) * 100)}%"></span>{/if}
          </span>
          <span class="label">{String(i + 1).padStart(2, '0')} {t('stage.' + s)}</span>
        </li>
      {/each}
    </ol>

    <div class="guard">
      {#if task}
        <div class="status" in:rise>
          <span class="t-label tnum">{t('launch.elapsed', { t: clock(now - new Date(task.started).getTime()) })}</span>
          <span class="status-line">{t('launch.launching', { name: instance.name })}</span>
        </div>
        <button class="btn lg" onclick={abort}>{t('launch.abort')}</button>
      {:else if session}
        <div class="status" in:rise>
          <span class="t-label"><span class="lamp {session.state === 'running' ? 'ok' : 'caution pulse'}"></span> {t('telemetry.state.' + session.state)}</span>
          <span class="status-line">{t('launch.running', { name: instance.name })}</span>
        </div>
        <button class="btn lg" onclick={() => { app.telemetrySession = session.id; go('telemetry'); }}><Icon name="terminal" />{t('launch.openLog')}</button>
      {:else}
        <div class="hazard frame" in:rise>
          <button class="ignite" onclick={() => launch(instance)} disabled={!!instance.error}>
            <span class="words">
              <span class="big">{t('launch.ignite')}</span>
              <span class="small">{t('launch.igniteSub')}</span>
            </span>
            <kbd>Enter</kbd>
          </button>
        </div>
      {/if}
    </div>
  </footer>
{/if}

<style>
  .empty-state { flex: 1; display: grid; place-items: center; }
  .deck { flex: 1; min-height: 0; display: grid; grid-template-columns: minmax(0, 1.25fr) minmax(320px, 1fr); }
  .deck > :global(.map) { margin: 24px 0 24px 28px; }
  .readout { padding: 24px 28px; display: flex; flex-direction: column; gap: 20px; min-width: 0; overflow: auto; }
  .identity { display: flex; flex-direction: column; gap: 8px; }
  .plate { display: grid; overflow: hidden; }
  .plate > :global(*) { grid-area: 1 / 1; }
  .name { font-size: clamp(32px, 4.2vw, 52px); overflow-wrap: anywhere; }
  .readings :global(dt), .readings :global(dd) { animation: reading 380ms var(--ease) both; }
  .readings :global(dt:nth-of-type(1)), .readings :global(dt:nth-of-type(1) + dd) { animation-delay: 80ms; }
  .readings :global(dt:nth-of-type(2)), .readings :global(dt:nth-of-type(2) + dd) { animation-delay: 130ms; }
  .readings :global(dt:nth-of-type(3)), .readings :global(dt:nth-of-type(3) + dd) { animation-delay: 180ms; }
  .readings :global(dt:nth-of-type(4)), .readings :global(dt:nth-of-type(4) + dd) { animation-delay: 230ms; }
  @keyframes reading { from { opacity: 0; transform: translateX(6px); } }
  .chips { display: flex; gap: 8px; flex-wrap: wrap; }
  .actions { display: flex; gap: 8px; flex-wrap: wrap; }
  .broken { display: flex; gap: 8px; align-items: flex-start; color: var(--warning); font-size: 13px; }

  .ignition { flex: none; display: grid; grid-template-columns: minmax(0, 1fr) 360px; border-top: 1px solid var(--rule); background: var(--panel-2); }
  .sequence { padding: 20px 28px; display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 6px; align-content: center; }
  .sequence li { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
  .track { height: 4px; background: var(--rule); position: relative; overflow: hidden; }
  /* The active stage fills toward its progress with a light sweeping along it; finished stages snap to green. */
  .fill { position: absolute; inset: 0 auto 0 0; background: var(--accent); transition: width 260ms var(--ease); overflow: hidden; }
  .fill::after { content: ''; position: absolute; inset: 0; background: linear-gradient(90deg, transparent, rgba(255, 255, 255, 0.55), transparent); width: 40%; animation: scan 900ms linear infinite; }
  @keyframes scan { from { transform: translateX(-100%); } to { transform: translateX(260%); } }
  .done-fill { position: absolute; inset: 0; background: var(--nominal); transform-origin: left; animation: complete 360ms var(--ease) both; }
  @keyframes complete { from { transform: scaleX(0); } 70% { filter: brightness(1.8); } }
  .active .label::before { content: ''; display: inline-block; width: 6px; height: 6px; margin-right: 6px; background: var(--accent); vertical-align: 1px; animation: lamp 0.8s steps(2, jump-none) infinite; }
  .label { font-size: 12px; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .done .label { color: var(--ink-2); }
  .active .label { color: var(--accent-ink); font-weight: 600; }

  .guard { padding: 16px 28px 16px 0; display: flex; align-items: center; gap: 16px; justify-content: flex-end; }
  .status { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
  .status .lamp { display: inline-block; vertical-align: middle; }
  .status .lamp.ok { animation: heartbeat 1.6s var(--ease) infinite; }
  @keyframes heartbeat { 0%, 100% { box-shadow: 0 0 0 0 color-mix(in oklab, var(--nominal) 60%, transparent); } 50% { box-shadow: 0 0 0 6px transparent; } }
  .status-line { font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  /* The guard: stripes start to march when the pointer arms the switch. */
  .frame { flex: 1; padding: 6px; background-size: 28.28px 28.28px; transition: padding var(--t-fast) var(--ease); }
  .frame:hover, .frame:focus-within { animation: march 600ms linear infinite; }
  .frame:has(.ignite:active) { padding: 8px; }
  @keyframes march { to { background-position: 28.28px 0; } }
  .ignite {
    width: 100%; height: 68px; border: 0; cursor: pointer; background: var(--accent-fill); color: var(--on-accent);
    display: flex; align-items: center; justify-content: space-between; padding: 0 20px;
    box-shadow: inset 0 -4px 0 rgba(0, 0, 0, 0.25); transition: transform 90ms var(--ease), filter var(--t-fast), box-shadow 90ms;
    position: relative; overflow: hidden;
  }
  .ignite::after { content: ''; position: absolute; inset: 0; background: radial-gradient(circle at var(--px, 50%) 50%, rgba(255, 255, 255, 0.5), transparent 60%); opacity: 0; pointer-events: none; }
  .ignite:active:not(:disabled)::after { opacity: 1; transition: none; }
  .ignite:not(:active)::after { transition: opacity 500ms var(--ease); }
  .ignite:hover:not(:disabled) { filter: brightness(1.08); }
  .ignite:active:not(:disabled) { transform: translateY(2px); box-shadow: inset 0 -1px 0 rgba(0, 0, 0, 0.25); }
  .ignite:disabled { filter: grayscale(1); opacity: 0.5; cursor: not-allowed; }
  .ignite:focus-visible { outline: 2px solid var(--ink); outline-offset: 2px; }
  .words { display: flex; flex-direction: column; align-items: flex-start; gap: 2px; }
  .big { font-size: 22px; font-weight: 900; font-stretch: 118%; }
  .small { font-size: 11px; font-weight: 700; letter-spacing: 0.14em; font-stretch: 80%; }
  kbd { font-family: var(--font-mono); font-size: 12px; font-weight: 600; border: 1.5px solid currentColor; padding: 3px 7px; }

  @media (max-width: 1180px) {
    .ignition { grid-template-columns: 1fr; }
    .guard { padding: 0 28px 16px; }
  }
</style>
