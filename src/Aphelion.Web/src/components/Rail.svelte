<script lang="ts">
  import { app, go, routes, runningTasks, selectedAccount, liveSessions, type Route } from '../lib/app.svelte';
  import { t } from '../lib/i18n/index.svelte';
  import { bytes } from '../lib/format';
  import PlayerHead from './PlayerHead.svelte';
  import Mark from './Mark.svelte';
  import { glide, slide } from '../lib/motion';

  const labels: Record<Route, string> = {
    launch: 'nav.launch', instances: 'nav.instances', download: 'nav.download',
    accounts: 'nav.accounts', telemetry: 'nav.telemetry', settings: 'nav.settings',
  };

  const account = $derived(selectedAccount());
  const tasks = $derived(runningTasks().filter((x) => x.kind !== 'launch'));
  const lead = $derived(tasks[0]);
  const gameLive = $derived(liveSessions().length > 0);

  function lampFor(r: Route) {
    if (r === app.route) return 'lamp accent';
    if (r === 'telemetry' && gameLive) return 'lamp caution pulse';
    if (r === 'download' && tasks.length > 0) return 'lamp caution';
    if (r === 'accounts' && account?.needsSignIn) return 'lamp warning';
    return 'lamp hollow';
  }
</script>

<nav class="rail" aria-label={t('nav.main')}>
  <div class="brand drag">
    <Mark size={34} active={tasks.length > 0 || gameLive || runningTasks().length > 0} />
    <div class="brand-text">
      <span class="name">APHELION</span>
      <span class="ver">Minecraft · {app.info?.version ?? ''}</span>
    </div>
  </div>

  <ul class="nav" use:glide={{ axis: 'y' }}>
    {#each routes as r, i (r)}
      <li style:--i={i}>
        <button class:active={app.route === r} aria-current={app.route === r ? 'page' : undefined} onclick={() => go(r)} title="Ctrl+{i + 1}">
          <span class={lampFor(r)}></span>
          <span class="label">{t(labels[r])}</span>
          <kbd>{i + 1}</kbd>
        </button>
      </li>
    {/each}
  </ul>

  {#if lead}
    <button class="task" onclick={() => go('download')} transition:slide={{ x: -16 }}>
      <span class="t-label">{t('tasks.title')} · {t('ann.tasks.running', { n: tasks.length })}</span>
      <span class="task-title ellipsis">{lead.title}</span>
      <span class="bar live" class:indeterminate={lead.fraction == null}><span style:width="{(lead.fraction ?? 0) * 100}%"></span></span>
      <span class="t-label tnum ellipsis">
        {lead.stage ? t('stage.' + lead.stage) : ''}{lead.bytesPerSecond > 0 ? ` · ${bytes(lead.bytesPerSecond)}/s` : ''}
      </span>
    </button>
  {/if}

  <button class="crew" onclick={() => go('accounts')}>
    {#if account}
      <PlayerHead skinUrl={account.skinUrl} uuid={account.uuid} size={40} />
      <span class="crew-text">
        <span class="crew-name ellipsis">{account.name}</span>
        <span class="crew-state" class:bad={account.needsSignIn}>
          <span class="lamp {account.needsSignIn ? 'warning' : account.kind === 'offline' ? 'hollow' : 'ok'}"></span>
          {t('accounts.kind.' + account.kind)}{account.needsSignIn ? ' · ' + t('accounts.status.relogin') : ''}
        </span>
      </span>
    {:else}
      <span class="crew-empty">+</span>
      <span class="crew-text"><span class="crew-name">{t('launch.noAccount')}</span><span class="crew-state">{t('launch.addAccount')}</span></span>
    {/if}
  </button>
</nav>

<style>
  .rail { display: flex; flex-direction: column; background: var(--panel-2); border-right: 1px solid var(--rule); min-height: 0; }
  .brand { height: var(--header-h); display: flex; align-items: center; gap: 12px; padding: 0 20px; border-bottom: 1px solid var(--rule); flex: none; }
  .brand-text { display: flex; flex-direction: column; gap: 2px; }
  .name { font-size: 17px; font-weight: 800; font-stretch: 125%; letter-spacing: 0.06em; }
  .ver { font-size: 11px; color: var(--muted); font-stretch: 80%; }
  .nav { padding: 14px 10px; display: flex; flex-direction: column; gap: 2px; position: relative; }
  /* One highlight that travels between items (motion.ts › glide). */
  .nav :global(.glide-y) { left: 10px; right: 10px; top: 0; background: var(--panel-raised); box-shadow: inset 2px 0 0 var(--accent); z-index: 0; }
  .nav li { position: relative; z-index: 1; }
  .nav button {
    all: unset; box-sizing: border-box; width: 100%; cursor: pointer; display: grid; grid-template-columns: 14px 1fr auto; align-items: center; gap: 12px;
    height: 44px; padding: 0 12px; color: var(--ink-2); font-size: 15px; transition: color var(--t-fast), background var(--t-fast);
  }
  .nav button:hover:not(.active) { background: color-mix(in oklab, var(--panel-raised) 55%, transparent); color: var(--ink); }
  .nav .label { transition: transform var(--t-base) var(--ease); }
  .nav button.active .label { transform: translateX(3px); }
  .nav button:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
  .nav button.active { color: var(--ink); font-weight: 600; }
  kbd { font-family: var(--font-mono); font-size: 11px; color: var(--faint); border: 1px solid var(--rule); padding: 0 5px; line-height: 18px; }
  .active kbd { color: var(--muted); border-color: var(--rule-strong); }

  .task { all: unset; box-sizing: border-box; cursor: pointer; margin: auto 10px 0; padding: 12px; display: flex; flex-direction: column; gap: 6px; border: 1px solid var(--rule); background: var(--screen); }
  .task:hover { border-color: var(--rule-strong); }
  .task-title { font-size: 13px; font-weight: 600; }
  .task .bar { height: 4px; }

  .crew { all: unset; box-sizing: border-box; cursor: pointer; margin-top: auto; padding: 16px; border-top: 1px solid var(--rule); display: grid; grid-template-columns: 40px minmax(0, 1fr); gap: 12px; align-items: center; }
  .task + .crew { margin-top: 10px; }
  .crew:hover { background: var(--panel-raised); }
  .crew:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
  .crew-text { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .crew-name { font-size: 14px; font-weight: 600; }
  .crew-state { font-size: 12px; color: var(--muted); display: flex; align-items: center; gap: 6px; }
  .crew-state.bad { color: var(--warning); }
  .crew-state .lamp { width: 6px; height: 6px; }
  .crew-empty { width: 40px; height: 40px; border: 1px dashed var(--rule-strong); display: grid; place-items: center; color: var(--muted); font-size: 20px; }
</style>
