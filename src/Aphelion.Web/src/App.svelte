<script lang="ts">
  import { onMount } from 'svelte';
  import { app, fail, go, routes, start } from './lib/app.svelte';
  import { t } from './lib/i18n/index.svelte';
  import Rail from './components/Rail.svelte';
  import WindowControls from './components/WindowControls.svelte';
  import Toasts from './components/Toasts.svelte';
  import CreateInstance from './views/CreateInstance.svelte';
  import Launch from './views/Launch.svelte';
  import Instances from './views/Instances.svelte';
  import Download from './views/Download.svelte';
  import Accounts from './views/Accounts.svelte';
  import Telemetry from './views/Telemetry.svelte';
  import SettingsView from './views/Settings.svelte';

  let failed = $state<string | null>(null);

  onMount(() => {
    start().catch((e) => {
      failed = String(e?.message ?? e);
      fail(e);
    });
  });

  function onKey(e: KeyboardEvent) {
    if (!e.ctrlKey || e.altKey || e.metaKey) return;
    const n = Number(e.key);
    if (n >= 1 && n <= routes.length) {
      e.preventDefault();
      go(routes[n - 1]);
    }
  }
</script>

<svelte:window onkeydown={onKey} />

<WindowControls />

{#if app.ready}
  <div class="shell">
    <Rail />
    <main>
      {#if app.route === 'launch'}<Launch />
      {:else if app.route === 'instances'}<Instances />
      {:else if app.route === 'download'}<Download />
      {:else if app.route === 'accounts'}<Accounts />
      {:else if app.route === 'telemetry'}<Telemetry />
      {:else}<SettingsView />{/if}
    </main>
  </div>
  <CreateInstance />
{:else}
  <div class="boot drag">
    <svg width="96" height="96" viewBox="0 0 34 34" aria-hidden="true">
      <ellipse cx="17" cy="17" rx="13" ry="8" fill="none" stroke="var(--rule-strong)" stroke-width="1" />
      <circle cx="7.4" cy="17" r="2.2" fill="var(--ink)" />
      <circle r="2.4" fill="var(--accent)"><animateMotion dur="2.4s" repeatCount="indefinite" path="M30,17 A13,8 0 1,1 4,17 A13,8 0 1,1 30,17" /></circle>
    </svg>
    {#if failed}<p class="selectable">{failed}</p>{:else}<p class="t-legend">{t('common.loading')}</p>{/if}
  </div>
{/if}

<Toasts />

<style>
  .shell { height: 100%; display: grid; grid-template-columns: var(--rail-w) minmax(0, 1fr); }
  main { min-width: 0; min-height: 0; display: flex; flex-direction: column; overflow: hidden; }
  .boot { height: 100%; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 16px; color: var(--muted); }
</style>
