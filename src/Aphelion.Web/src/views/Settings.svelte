<script lang="ts">
  import { onMount } from 'svelte';
  import { app, fail, refreshJava, reportTask, toast, updateSettings } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { t } from '../lib/i18n/index.svelte';
  import { bytes, megabytes } from '../lib/format';
  import type { StorageInfo } from '../lib/types';
  import ViewHeader from '../components/ViewHeader.svelte';
  import Icon from '../components/Icon.svelte';
  import ModeSwitch from './ModeSwitch.svelte';
  import NetworkCheck from './NetworkCheck.svelte';
  import UpdatePanel from './UpdatePanel.svelte';
  import { glide, viewTransition } from '../lib/motion';

  const sections = ['appearance', 'download', 'java', 'game', 'accounts', 'storage', 'about'] as const;
  const accents = ['#FF5B14', '#F2B33D', '#4FA3FF', '#6FCF97', '#B48CFF', '#E6ECE8'];

  let active = $state<string>('appearance');
  let scroller: HTMLDivElement;
  let storage = $state<StorageInfo | null>(null);
  let scanning = $state(false);
  let switchTo = $state<'standard' | 'portable' | null>(null);
  let switchOpen = $state(false);

  const s = $derived(app.settings!);
  const maxMb = $derived(Math.max(2048, Math.floor(((app.memory?.totalMb ?? 16384) * 0.75) / 256) * 256));

  // Text fields save when they lose focus, not on every keystroke.
  let jvmArgs = $state(app.settings?.game.jvmArguments ?? '');
  let clientId = $state(app.settings?.microsoftClientId ?? '');
  let fixedMb = $state(app.settings?.game.memory.fixedMb ?? 4096);
  let width = $state(app.settings?.game.resolution.width ?? 1280);
  let height = $state(app.settings?.game.resolution.height ?? 720);

  onMount(() => {
    call<StorageInfo>('app.storage').then((x) => (storage = x)).catch(() => {});
  });

  function jump(id: string) {
    active = id;
    document.getElementById('set-' + id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function onScroll() {
    for (const id of sections) {
      const el = document.getElementById('set-' + id);
      if (el && el.getBoundingClientRect().top - scroller.getBoundingClientRect().top < 120) active = id;
    }
  }

  /** The new theme opens as a circle from the pressed control (motion.ts › viewTransition). */
  function switchTheme(theme: string, e: MouseEvent) {
    // Keyboard activation reports (0, 0); start from the button itself then.
    const box = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const origin = e.clientX || e.clientY ? { x: e.clientX, y: e.clientY } : { x: box.left + box.width / 2, y: box.top + box.height / 2 };
    viewTransition(() => updateSettings({ theme }), 'theme', origin);
  }

  async function rescan() {
    scanning = true;
    await refreshJava(true).catch(fail);
    scanning = false;
  }

  async function addJava() {
    try {
      const j = await call('java.add');
      if (j) await refreshJava(true);
    } catch (e) {
      fail(e);
    }
  }

  async function installJava(major: number) {
    try {
      const id = await call<string>('java.install', { major });
      reportTask(id, () => { refreshJava(true); return t('settings.java.install', { n: major }) + ' ✓'; });
    } catch (e) {
      fail(e);
    }
  }

  async function clearCache() {
    try {
      const freed = await call<number>('app.clearCache');
      toast(t('settings.storage.cleared', { v: bytes(freed) }), 'success');
      storage = await call<StorageInfo>('app.storage');
    } catch (e) {
      fail(e);
    }
  }
</script>

<ViewHeader title={t('settings.title')} />

{#if switchTo}<ModeSwitch bind:open={switchOpen} to={switchTo} />{/if}

<div class="layout">
  <nav class="index" aria-label={t('settings.title')} use:glide={{ axis: 'y' }}>
    {#each sections as id (id)}
      <button class:on={active === id} aria-current={active === id ? 'page' : undefined} onclick={() => jump(id)}>{t('settings.section.' + id)}</button>
    {/each}
  </nav>

  <div class="scroll" bind:this={scroller} onscroll={onScroll}>
    <section id="set-appearance">
      <h2 class="t-section">{t('settings.section.appearance')}</h2>
      <div class="row">
        <span class="label">{t('settings.language')}</span>
        <div class="seg">
          {#each [['auto', t('settings.language.auto')], ['zh-Hant', '繁體中文'], ['zh-Hans', '简体中文'], ['en', 'English']] as [v, label] (v)}
            <button aria-pressed={s.language === v} onclick={() => updateSettings({ language: v })}>{label}</button>
          {/each}
        </div>
      </div>
      <div class="row">
        <span class="label">{t('settings.theme')}</span>
        <div class="seg">
          {#each ['console', 'manual', 'system'] as v (v)}
            <button aria-pressed={s.theme === v} onclick={(e) => switchTheme(v, e)}>{t('settings.theme.' + v)}</button>
          {/each}
        </div>
      </div>
      <div class="row">
        <span class="label">{t('settings.accent')}</span>
        <div class="swatches">
          {#each accents as c (c)}
            <button class="swatch" style:background={c} aria-label={c} aria-pressed={s.accent.toLowerCase() === c.toLowerCase()} onclick={() => updateSettings({ accent: c })}></button>
          {/each}
        </div>
      </div>
      <div class="row top">
        <span class="label">{t('settings.motion')}</span>
        <div class="stack">
          <div class="seg">
            {#each ['system', 'full', 'reduced'] as v (v)}
              <button aria-pressed={(s.motion ?? 'system') === v} onclick={() => updateSettings({ motion: v })}>{t('settings.motion.' + v)}</button>
            {/each}
          </div>
          <p class="hint">{t('settings.motion.hint')}</p>
        </div>
      </div>
    </section>

    <section id="set-download">
      <h2 class="t-section">{t('settings.section.download')}</h2>
      <div class="cards two">
        <div class="card static"><strong>{t('settings.download.official')}</strong><span>{t('settings.download.official.body')}</span></div>
        <div class="card static"><strong>{t('settings.download.speed')}</strong><span>{t('settings.download.speed.body')}</span></div>
      </div>
      <div class="row">
        <span class="label">{t('settings.concurrency')}</span>
        <div class="slider">
          <input type="range" min="1" max="64" value={s.download.concurrency} onchange={(e) => updateSettings({ download: { concurrency: Number((e.target as HTMLInputElement).value) } })} aria-label={t('settings.concurrency')} />
          <span class="tnum value">{s.download.concurrency}</span>
        </div>
      </div>
      <NetworkCheck />
    </section>

    <section id="set-java">
      <h2 class="t-section">{t('settings.section.java')}</h2>
      <div class="row">
        <span class="label">{t('settings.java.default')}</span>
        <select class="input wide" value={s.game.javaPath ?? ''} onchange={(e) => updateSettings({ game: { javaPath: (e.target as HTMLSelectElement).value || null } })}>
          <option value="">{t('settings.java.auto')}</option>
          {#each app.java as j (j.executable)}<option value={j.executable}>Java {j.major} · {j.version} · {j.arch}</option>{/each}
        </select>
      </div>
      {#if app.java.length === 0}
        <p class="hint">{t('settings.java.none')}</p>
      {:else}
        <ul class="screen java">
          {#each app.java as j (j.executable)}
            <li>
              <span class="major tnum">{j.major}</span>
              <span class="jmeta"><span>{j.version} · {j.arch}{j.vendor ? ' · ' + j.vendor : ''}</span><span class="t-mono path selectable">{j.home}</span></span>
              <span class="chip">{t('settings.java.source.' + j.source)}</span>
            </li>
          {/each}
        </ul>
      {/if}
      <div class="buttons">
        <button class="btn sm" disabled={scanning} onclick={rescan}><Icon name="refresh" size={14} />{t('settings.java.rescan')}</button>
        <button class="btn sm" onclick={addJava}><Icon name="plus" size={14} />{t('settings.java.add')}</button>
        {#each [8, 17, 21] as m (m)}<button class="btn sm" onclick={() => installJava(m)}><Icon name="download" size={14} />{t('settings.java.install', { n: m })}</button>{/each}
      </div>
    </section>

    <section id="set-game">
      <h2 class="t-section">{t('settings.section.game')}</h2>
      <div class="row top">
        <span class="label">{t('settings.memory')}</span>
        <div class="stack">
          <div class="seg">
            <button aria-pressed={s.game.memory.mode === 'auto'} onclick={() => updateSettings({ game: { memory: { mode: 'auto' } } })}>{t('common.auto')}</button>
            <button aria-pressed={s.game.memory.mode === 'fixed'} onclick={() => updateSettings({ game: { memory: { mode: 'fixed' } } })}>{t('overrides.memory.fixed')}</button>
          </div>
          {#if s.game.memory.mode === 'fixed'}
            <div class="slider">
              <input type="range" min="1024" max={maxMb} step="256" bind:value={fixedMb} onchange={() => updateSettings({ game: { memory: { fixedMb } } })} aria-label={t('settings.memory')} />
              <span class="tnum value">{megabytes(fixedMb)}</span>
            </div>
          {:else}
            <p class="hint">{t('settings.memory.auto.hint')}</p>
          {/if}
          {#if app.memory}<p class="hint tnum">{t('settings.memory.total', { total: megabytes(app.memory.totalMb), free: megabytes(app.memory.availableMb) })}</p>{/if}
        </div>
      </div>
      <div class="row">
        <span class="label">{t('settings.resolution')}</span>
        <div class="res">
          <input class="input tnum" type="number" min="320" bind:value={width} onchange={() => updateSettings({ game: { resolution: { width } } })} aria-label="width" />
          <span class="muted">×</span>
          <input class="input tnum" type="number" min="240" bind:value={height} onchange={() => updateSettings({ game: { resolution: { height } } })} aria-label="height" />
          <label class="check"><input type="checkbox" checked={s.game.resolution.fullscreen} onchange={(e) => updateSettings({ game: { resolution: { fullscreen: (e.target as HTMLInputElement).checked } } })} />{t('overrides.fullscreen')}</label>
        </div>
      </div>
      <div class="row">
        <span class="label">{t('settings.afterLaunch')}</span>
        <div class="seg">
          {#each ['keep', 'minimize', 'close'] as v (v)}
            <button aria-pressed={s.game.afterLaunch === v} onclick={() => updateSettings({ game: { afterLaunch: v } })}>{t('settings.afterLaunch.' + v)}</button>
          {/each}
        </div>
      </div>
      <div class="row">
        <span class="label">{t('settings.analyzeCrashes')}</span>
        <button class="switch" role="switch" aria-checked={s.game.analyzeCrashes} aria-label={t('settings.analyzeCrashes')} onclick={() => updateSettings({ game: { analyzeCrashes: !s.game.analyzeCrashes } })}></button>
      </div>
      <div class="row top">
        <span class="label">{t('overrides.jvm')}</span>
        <div class="stack">
          <textarea class="input" rows="2" bind:value={jvmArgs} onblur={() => jvmArgs !== s.game.jvmArguments && updateSettings({ game: { jvmArguments: jvmArgs } })} spellcheck="false"></textarea>
          <p class="hint">{t('overrides.jvm.hint')}</p>
        </div>
      </div>
    </section>

    <section id="set-accounts">
      <h2 class="t-section">{t('settings.section.accounts')}</h2>
      <div class="row top">
        <span class="label">{t('settings.msClientId')}</span>
        <div class="stack">
          <input class="input t-mono" bind:value={clientId} placeholder="00000000-0000-0000-0000-000000000000" spellcheck="false"
            onblur={() => clientId !== s.microsoftClientId && updateSettings({ microsoftClientId: clientId })} />
          <p class="hint">{t('settings.msClientId.hint')}</p>
        </div>
      </div>
    </section>

    <section id="set-storage">
      <h2 class="t-section">{t('settings.section.storage')}</h2>
      <div class="cards two">
        {#each ['standard', 'portable'] as const as m (m)}
          {@const current = app.info?.mode === m}
          <button class="card mode" aria-current={current} disabled={current || app.info?.mode === 'override'}
            onclick={() => { switchTo = m; switchOpen = true; }}>
            <strong><span class="lamp {current ? 'ok' : 'hollow'}"></span>{t('settings.mode.' + m)}{current ? ` · ${t('settings.mode.current')}` : ''}</strong>
            <span>{t('settings.mode.' + m + '.hint')}</span>
            {#if !current && app.info?.mode !== 'override'}<span class="go">{t('settings.mode.switch')}<Icon name="chevronRight" size={14} /></span>{/if}
          </button>
        {/each}
      </div>
      {#if app.info?.mode === 'override'}<p class="hint">{t('settings.mode.override.hint')}</p>{/if}
      {#if app.info?.mode === 'standard'}
        <div class="row">
          <span class="label">{app.info.installed ? t('settings.installed', { v: app.info.installedVersion ?? app.info.version }) : t('settings.mode.notInstalled')}</span>
          {#if !app.info.installed}<button class="btn sm" onclick={() => call('app.install').catch(fail)}><Icon name="download" size={14} />{t('settings.install')}</button>{/if}
        </div>
      {/if}

      <h3 class="t-label sub">{t('settings.storage.where')}</h3>
      {#if storage}
        <dl class="screen paths">
          <dt>{t('settings.storage.config')}</dt><dd class="t-mono selectable">{storage.paths.config}</dd><dd class="tnum">{bytes(storage.config)}</dd>
          <dt>{t('settings.storage.games')}</dt><dd class="t-mono selectable">{s.gameRoots.map((r) => r.path).join('\n')}</dd><dd></dd>
          <dt>{t('settings.storage.runtime')}</dt><dd class="t-mono selectable">{storage.paths.runtime}</dd><dd class="tnum">{bytes(storage.runtime)}</dd>
          <dt>{t('settings.storage.cache')}</dt><dd class="t-mono selectable">{storage.paths.cache}</dd><dd class="tnum">{bytes(storage.cache)}</dd>
          <dt>{t('settings.storage.logs')}</dt><dd class="t-mono selectable">{storage.paths.logs}</dd><dd class="tnum">{bytes(storage.logs)}</dd>
        </dl>
      {/if}
      <div class="buttons">
        <button class="btn sm" onclick={clearCache}>{t('settings.storage.clearCache')}</button>
        <button class="btn sm" onclick={() => call('app.openFolder', { kind: 'data' }).catch(fail)}><Icon name="folder" size={14} />{t('settings.storage.openData')}</button>
      </div>

      <div class="uninstall">
        <h3 class="t-section">{t('settings.uninstall')}</h3>
        <p>{app.info?.installed ? t('settings.uninstall.body') : t('settings.uninstall.notInstalled')}</p>
        {#if app.info?.installed}<button class="btn" onclick={() => call('app.uninstall').catch(fail)}>{t('settings.uninstall.action')}</button>{/if}
      </div>
    </section>

    <section id="set-about">
      <h2 class="t-section">{t('settings.section.about')}</h2>
      <UpdatePanel />
      <dl class="telemetry">
        <dt>{t('settings.about.version')}</dt><dd>Aphelion {app.info?.version}</dd>
        <dt>{t('settings.about.runtime')}</dt><dd>.NET {app.info?.runtime} · {app.info?.os}</dd>
        <dt>{t('settings.about.log')}</dt><dd class="t-mono selectable small">{app.info?.logFile ?? '—'}</dd>
      </dl>
      <div class="buttons"><button class="btn sm" onclick={() => call('app.openFolder', { kind: 'logs' }).catch(fail)}><Icon name="folder" size={14} />{t('settings.about.openLogs')}</button></div>
      <p class="hint">{t('settings.about.credits')}</p>
    </section>
  </div>
</div>

<style>
  .layout { flex: 1; min-height: 0; display: grid; grid-template-columns: 200px minmax(0, 1fr); }
  .index { padding: 20px 0 0 28px; display: flex; flex-direction: column; gap: 2px; }
  .index { position: relative; }
  .index :global(.glide-y) { left: 16px; width: 2px; top: 0; background: var(--accent); }
  .index button { all: unset; cursor: pointer; padding: 8px 0; font-size: 14px; color: var(--ink-3); transition: color var(--t-fast), transform var(--t-base) var(--ease); }
  .index button.on { transform: translateX(4px); }
  .index button:hover { color: var(--ink); }
  .index button.on { color: var(--accent-ink); font-weight: 700; }
  .index button:focus-visible { outline: 2px solid var(--accent); }
  .scroll { overflow: auto; padding: 20px 40px 120px 20px; }
  section { max-width: 820px; display: flex; flex-direction: column; gap: 14px; padding-bottom: 36px; margin-bottom: 36px; border-bottom: 1px solid var(--rule); }
  section:last-child { border-bottom: 0; }
  .row { display: grid; grid-template-columns: 200px minmax(0, 1fr); gap: 16px; align-items: center; }
  .row.top { align-items: start; }
  .row > .label { font-size: 14px; color: var(--ink-3); }
  .row > :last-child { justify-self: start; }
  .stack { display: flex; flex-direction: column; gap: 8px; width: 100%; }
  .stack > .seg { align-self: flex-start; }
  .hint { font-size: 12px; color: var(--muted); line-height: 1.55; }
  .swatches { display: flex; gap: 6px; }
  .swatch { width: 28px; height: 28px; border: 0; cursor: pointer; padding: 0; outline-offset: 2px; transition: transform var(--t-base) var(--ease-spring), outline-offset var(--t-base) var(--ease); }
  .swatch:hover { transform: scale(1.12); }
  .swatch[aria-pressed='true'] { outline-offset: 3px; }
  .swatch[aria-pressed='true'] { outline: 2px solid var(--ink); }
  .cards { display: grid; gap: 8px; }
  .cards.two { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .card { display: flex; flex-direction: column; gap: 6px; padding: 14px; background: var(--panel-raised); border: 1px solid var(--rule); text-align: left; font-size: 13px; color: var(--muted); line-height: 1.5; }
  button.card { cursor: pointer; }
  .card strong { font-size: 15px; color: var(--ink); }
  .card[aria-current='true'] { border: 1.5px solid var(--accent); }
  button.card { transition: border-color var(--t-fast), background var(--t-fast), transform var(--t-fast) var(--ease); }
  button.card:hover:not(:disabled) { border-color: var(--rule-strong); background: color-mix(in oklab, var(--panel-raised) 70%, var(--accent-wash)); }
  button.card:active:not(:disabled) { transform: translateY(1px); }
  button.card:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
  .card.mode:disabled { cursor: default; color: var(--muted); }
  .card.mode strong { display: flex; align-items: center; gap: 8px; }
  .card.mode .lamp { width: 7px; height: 7px; }
  .go { margin-top: 4px; display: inline-flex; align-items: center; gap: 4px; color: var(--accent-ink); font-weight: 600; }
  .go :global(svg) { transition: transform var(--t-base) var(--ease); }
  button.card:hover .go :global(svg) { transform: translateX(3px); }
  .slider { display: flex; align-items: center; gap: 16px; width: 100%; max-width: 420px; }
  .slider input { flex: 1; accent-color: var(--accent); }
  .value { font-weight: 700; min-width: 64px; }
  .wide { max-width: 480px; }
  .java { display: flex; flex-direction: column; }
  .java li { display: grid; grid-template-columns: 44px minmax(0, 1fr) auto; gap: 14px; align-items: center; padding: 10px 14px; border-bottom: 1px solid var(--rule-faint); }
  .java li:last-child { border-bottom: 0; }
  .major { font-size: 22px; font-weight: 800; font-stretch: 118%; }
  .jmeta { display: flex; flex-direction: column; gap: 2px; min-width: 0; font-size: 13px; }
  .path { font-size: 11px; color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .buttons { display: flex; gap: 8px; flex-wrap: wrap; }
  .res { display: flex; align-items: center; gap: 10px; }
  .res .input { width: 110px; }
  .check { display: flex; align-items: center; gap: 8px; margin-left: 12px; cursor: pointer; }
  .check input { accent-color: var(--accent); }
  .sub { margin-top: 8px; }
  .paths { display: grid; grid-template-columns: 140px minmax(0, 1fr) auto; font-size: 13px; }
  .paths > * { padding: 11px 14px; border-bottom: 1px solid var(--rule-faint); margin: 0; }
  .paths dt { color: var(--muted); }
  .paths dd.t-mono { font-size: 12px; white-space: pre-line; overflow-wrap: anywhere; padding-left: 0; }
  .paths dd.tnum { color: var(--muted); }
  .uninstall { margin-top: 12px; padding: 16px; border: 1px solid var(--rule); display: flex; flex-direction: column; gap: 10px; align-items: flex-start;
    background: repeating-linear-gradient(-45deg, transparent 0 12px, color-mix(in oklab, var(--accent) 6%, transparent) 12px 24px); }
  .uninstall p { font-size: 13px; line-height: 1.6; color: var(--ink-3); max-width: 60em; }
  .small { font-size: 12px; }
  @media (max-width: 1240px) and (min-width: 1181px) { .row { grid-template-columns: 160px minmax(0, 1fr); } }
  @media (max-width: 960px) { .row { grid-template-columns: 1fr; gap: 8px; } }
  @media (max-width: 1040px) { .layout { grid-template-columns: 160px minmax(0, 1fr); } .index { padding-left: 22px; } .scroll { padding-right: 28px; } .cards.two { grid-template-columns: 1fr; } }
</style>
