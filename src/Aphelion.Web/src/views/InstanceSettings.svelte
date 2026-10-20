<script lang="ts">
  import { untrack } from 'svelte';
  import { app, fail, refreshInstances, toast } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { t } from '../lib/i18n/index.svelte';
  import { megabytes } from '../lib/format';
  import type { Instance, InstanceOverrides } from '../lib/types';

  let { instance }: { instance: Instance } = $props();

  const palette = ['#FF5B14', '#4FA3FF', '#F2B33D', '#6FCF97', '#B48CFF', '#5FD4D0', '#E6ECE8', '#FF8A57'];
  // The form is seeded once per instance (the parent re-creates it with {#key} when the instance changes).
  const init = untrack(() => ({ ...instance }));
  const o = init.overrides ?? {};

  let displayName = $state(init.name === init.id ? '' : init.name);
  let color = $state(init.color);
  let isolated = $state(init.isolated);
  let memoryMode = $state<'inherit' | 'auto' | 'fixed'>(o.memory ? o.memory.mode : 'inherit');
  let memoryMb = $state(o.memory?.fixedMb ?? app.settings?.game.memory.fixedMb ?? 4096);
  let javaPath = $state<string>(o.javaPath ?? '');
  let resMode = $state<'inherit' | 'custom'>(o.resolution ? 'custom' : 'inherit');
  let width = $state(o.resolution?.width ?? app.settings?.game.resolution.width ?? 1280);
  let height = $state(o.resolution?.height ?? app.settings?.game.resolution.height ?? 720);
  let fullscreen = $state(o.resolution?.fullscreen ?? false);
  let jvm = $state(o.jvmArguments ?? '');
  let game = $state(o.gameArguments ?? '');
  let server = $state(o.autoJoinServer ?? '');
  let saving = $state(false);

  const maxMb = $derived(Math.max(2048, Math.floor(((app.memory?.totalMb ?? 16384) * 0.75) / 256) * 256));

  async function save() {
    saving = true;
    const overrides: InstanceOverrides = {
      memory: memoryMode === 'inherit' ? null : { mode: memoryMode, fixedMb: memoryMb },
      javaPath: javaPath || null,
      resolution: resMode === 'inherit' ? null : { width, height, fullscreen },
      jvmArguments: jvm.trim() ? jvm.trim() : null,
      gameArguments: game.trim() ? game.trim() : null,
      autoJoinServer: server.trim() ? server.trim() : null,
    };
    try {
      await call('instances.update', { id: instance.id, displayName, color, isolated, overrides });
      toast(t('common.saved'), 'success');
      await refreshInstances();
    } catch (e) {
      fail(e);
    } finally {
      saving = false;
    }
  }
</script>

<form class="settings" onsubmit={(e) => { e.preventDefault(); save(); }}>
  <fieldset>
    <legend class="t-section">{t('instances.tab.overview')}</legend>
    <label class="field">
      <span class="label">{t('instances.displayName')}</span>
      <input class="input" bind:value={displayName} placeholder={instance.id} maxlength="60" />
    </label>
    <div class="field">
      <span class="label">{t('instances.color')}</span>
      <div class="swatches">
        {#each palette as c (c)}
          <button type="button" class="swatch" style:background={c} aria-pressed={color.toLowerCase() === c.toLowerCase()} aria-label={c} onclick={() => (color = c)}></button>
        {/each}
      </div>
    </div>
    <div class="toggle-row">
      <div><span>{t('instances.isolation')}</span><p class="hint">{t('instances.isolation.hint')}</p></div>
      <button type="button" class="switch" role="switch" aria-checked={isolated} aria-label={t('instances.isolation')} onclick={() => (isolated = !isolated)}></button>
    </div>
  </fieldset>

  <fieldset>
    <legend class="t-section">{t('overrides.memory')}</legend>
    <div class="seg">
      <button type="button" aria-pressed={memoryMode === 'inherit'} onclick={() => (memoryMode = 'inherit')}>{t('overrides.inherit')}</button>
      <button type="button" aria-pressed={memoryMode === 'auto'} onclick={() => (memoryMode = 'auto')}>{t('common.auto')}</button>
      <button type="button" aria-pressed={memoryMode === 'fixed'} onclick={() => (memoryMode = 'fixed')}>{t('overrides.memory.fixed')}</button>
    </div>
    {#if memoryMode === 'fixed'}
      <div class="slider">
        <input type="range" min="1024" max={maxMb} step="256" bind:value={memoryMb} aria-label={t('overrides.memory')} />
        <span class="tnum value">{megabytes(memoryMb)}</span>
      </div>
    {/if}
  </fieldset>

  <fieldset>
    <legend class="t-section">{t('overrides.java')}</legend>
    <select class="input" bind:value={javaPath}>
      <option value="">{t('overrides.inherit')} — {app.settings?.game.javaPath ?? t('settings.java.auto')}</option>
      {#each app.java as j (j.executable)}
        <option value={j.executable}>Java {j.major} · {j.version} · {j.arch} — {j.home}</option>
      {/each}
    </select>
  </fieldset>

  <fieldset>
    <legend class="t-section">{t('overrides.resolution')}</legend>
    <div class="seg">
      <button type="button" aria-pressed={resMode === 'inherit'} onclick={() => (resMode = 'inherit')}>{t('overrides.inherit')}</button>
      <button type="button" aria-pressed={resMode === 'custom'} onclick={() => (resMode = 'custom')}>{t('overrides.custom')}</button>
    </div>
    {#if resMode === 'custom'}
      <div class="res">
        <input class="input tnum" type="number" min="320" max="16384" bind:value={width} aria-label="width" />
        <span class="muted">×</span>
        <input class="input tnum" type="number" min="240" max="16384" bind:value={height} aria-label="height" />
        <label class="check"><input type="checkbox" bind:checked={fullscreen} />{t('overrides.fullscreen')}</label>
      </div>
    {/if}
  </fieldset>

  <fieldset>
    <legend class="t-section">{t('overrides.jvm')}</legend>
    <textarea class="input" rows="2" bind:value={jvm} placeholder={app.settings?.game.jvmArguments || '-XX:+UseZGC'} spellcheck="false"></textarea>
    <p class="hint">{t('overrides.jvm.hint')}</p>
    <label class="field">
      <span class="label">{t('overrides.game')}</span>
      <input class="input t-mono" bind:value={game} spellcheck="false" />
    </label>
    <label class="field">
      <span class="label">{t('overrides.server')}</span>
      <input class="input" bind:value={server} placeholder={t('overrides.server.placeholder')} spellcheck="false" />
    </label>
  </fieldset>

  <div class="save"><button class="btn primary" type="submit" disabled={saving}>{t('common.save')}</button></div>
</form>

<style>
  .settings { padding: 20px 28px 40px; display: flex; flex-direction: column; gap: 28px; max-width: 760px; }
  fieldset { border: 0; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 12px; }
  legend { padding: 0; margin-bottom: 12px; }
  .hint { font-size: 12px; color: var(--muted); }
  .swatches { display: flex; gap: 6px; }
  .swatch { width: 28px; height: 28px; border: 0; cursor: pointer; padding: 0; outline-offset: 2px; }
  .swatch[aria-pressed='true'] { outline: 2px solid var(--ink); }
  .toggle-row { display: flex; justify-content: space-between; gap: 16px; align-items: flex-start; }
  .slider { display: flex; align-items: center; gap: 16px; }
  .slider input { flex: 1; accent-color: var(--accent); }
  .value { font-weight: 700; min-width: 72px; }
  .res { display: flex; align-items: center; gap: 10px; }
  .res .input { width: 110px; }
  .check { display: flex; align-items: center; gap: 8px; margin-left: 12px; cursor: pointer; }
  .check input { accent-color: var(--accent); }
  .save { position: sticky; bottom: 0; padding: 12px 0; background: var(--panel); border-top: 1px solid var(--rule); }
</style>
