<script lang="ts">
  import { app, fail, reportTask, refreshInstances } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { describeError, t } from '../lib/i18n/index.svelte';
  import { date } from '../lib/format';
  import type { LoaderKind, LoaderVersion, ManifestVersion } from '../lib/types';
  import Dialog from '../components/Dialog.svelte';
  import Icon from '../components/Icon.svelte';

  const palette = ['#FF5B14', '#4FA3FF', '#F2B33D', '#6FCF97', '#B48CFF', '#5FD4D0', '#E6ECE8', '#FF8A57'];
  const loaders: LoaderKind[] = ['vanilla', 'fabric', 'quilt', 'forge', 'neoForge'];

  let versions = $state<ManifestVersion[]>([]);
  let filter = $state<'release' | 'snapshot' | 'old'>('release');
  let query = $state('');
  let gameVersion = $state<string | null>(null);
  let loader = $state<LoaderKind>('vanilla');
  let loaderVersions = $state<LoaderVersion[]>([]);
  let loaderVersion = $state<string | null>(null);
  let loaderState = $state<'idle' | 'loading' | 'none' | 'error'>('idle');
  let name = $state('');
  let nameTouched = $state(false);
  let color = $state(palette[0]);
  let isolated = $state(true);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let modpack = $state<null | { path: string; name: string; versionId: string; gameVersion: string; loader: LoaderKind; loaderVersion: string | null; files: number }>(null);

  $effect(() => {
    if (!app.createOpen) return;
    // Opened from the version list with a version chosen: start there.
    if (app.createVersion) {
      gameVersion = app.createVersion;
      app.createVersion = null;
    }
    if (versions.length === 0) {
      call<{ versions: ManifestVersion[] }>('versions.list', { refresh: false })
        .then((r) => {
          versions = r.versions;
          gameVersion ??= r.versions.find((v) => v.type === 'release')?.id ?? null;
          syncFilter();
        })
        .catch((e) => (error = describeError(e)));
    } else syncFilter();
  });

  function syncFilter() {
    const type = versions.find((v) => v.id === gameVersion)?.type;
    if (type) filter = type === 'release' ? 'release' : type === 'snapshot' ? 'snapshot' : 'old';
  }

  const shown = $derived(
    versions.filter((v) =>
      (filter === 'release' ? v.type === 'release' : filter === 'snapshot' ? v.type === 'snapshot' : v.type.startsWith('old_')) &&
      (!query || v.id.toLowerCase().includes(query.trim().toLowerCase())),
    ),
  );

  // Load loader versions whenever the game version or loader changes.
  $effect(() => {
    const gv = gameVersion;
    const kind = loader;
    loaderVersions = [];
    loaderVersion = null;
    if (!gv || kind === 'vanilla') { loaderState = 'idle'; return; }
    loaderState = 'loading';
    call<LoaderVersion[]>('loaders.list', { kind, gameVersion: gv })
      .then((list) => {
        if (gv !== gameVersion || kind !== loader) return;
        loaderVersions = list;
        loaderState = list.length ? 'idle' : 'none';
        loaderVersion = (list.find((l) => l.label === 'recommended') ?? list.find((l) => l.stable) ?? list[0])?.version ?? null;
      })
      .catch(() => (loaderState = 'error'));
  });

  // Suggest a name until the user types one.
  $effect(() => {
    if (nameTouched || modpack) return;
    name = gameVersion ? (loader === 'vanilla' ? gameVersion : `${gameVersion}-${t('loader.' + loader)}`) : '';
  });

  const nameProblem = $derived(
    !name.trim() ? 'empty'
      : /[\\/:*?"<>|]/.test(name) || /^(con|prn|aux|nul|com\d|lpt\d)$/i.test(name.trim()) || name.endsWith('.') ? 'invalid'
      : app.instances.some((i) => i.id.toLowerCase() === name.trim().toLowerCase()) ? 'exists' : null,
  );

  const canInstall = $derived(!busy && !nameProblem && (modpack || (gameVersion && (loader === 'vanilla' || loaderVersion))));

  function reset() {
    modpack = null;
    nameTouched = false;
    error = null;
    query = '';
    loader = 'vanilla';
  }

  async function pickModpack() {
    try {
      const picked = await call<typeof modpack>('modpack.pickFile');
      if (!picked) return;
      modpack = picked;
      name = picked.name.replace(/[\\/:*?"<>|]/g, ' ').trim();
      nameTouched = true;
    } catch (e) {
      error = describeError(e);
    }
  }

  async function install() {
    busy = true;
    error = null;
    const id = name.trim();
    try {
      const taskId = modpack
        ? await call<string>('modpack.install', { id, displayName: modpack.name, path: modpack.path })
        : await call<string>('instances.create', { id, gameVersion, loader, loaderVersion: loader === 'vanilla' ? null : loaderVersion, color, isolated });
      reportTask(taskId, () => {
        refreshInstances();
        return t('create.done', { name: id });
      });
      app.createOpen = false;
      reset();
    } catch (e) {
      error = describeError(e);
    } finally {
      busy = false;
    }
  }
</script>

<Dialog bind:open={app.createOpen} title={t('create.title')} width={880} onclose={reset}>
  {#if modpack}
    <div class="modpack screen">
      <Icon name="archive" size={20} />
      <div>
        <strong>{modpack.name}</strong> <span class="muted">{modpack.versionId}</span>
        <p class="muted">Minecraft {modpack.gameVersion} · {t('loader.' + modpack.loader)} {modpack.loaderVersion ?? ''} · {modpack.files}</p>
      </div>
      <button class="btn sm" onclick={() => { modpack = null; nameTouched = false; }}>{t('common.remove')}</button>
    </div>
  {:else}
    <div class="grid">
      <section class="col">
        <div class="col-head">
          <h3 class="t-label">{t('create.step.version')}</h3>
          <div class="seg">
            {#each ['release', 'snapshot', 'old'] as f (f)}
              <button aria-pressed={filter === f} onclick={() => (filter = f as typeof filter)}>{t('create.filter.' + f)}</button>
            {/each}
          </div>
        </div>
        <label class="search">
          <Icon name="search" size={14} />
          <input class="input" placeholder={t('create.searchVersion')} bind:value={query} />
        </label>
        <ul class="versions screen" role="listbox" aria-label={t('create.step.version')}>
          {#each shown as v (v.id)}
            <li>
              <button role="option" aria-selected={gameVersion === v.id} class:sel={gameVersion === v.id} onclick={() => (gameVersion = v.id)}>
                <span class="vid">{v.id}</span><span class="muted">{date(v.releaseTime)}</span>
              </button>
            </li>
          {:else}
            <li class="muted pad">{versions.length ? '—' : t('common.loading')}</li>
          {/each}
        </ul>
      </section>

      <section class="col">
        <h3 class="t-label">{t('create.step.loader')}</h3>
        <div class="loaders">
          {#each loaders as l (l)}
            <button class="loader" aria-pressed={loader === l} onclick={() => (loader = l)}>
              <span class="lamp {loader === l ? 'accent' : 'hollow'}"></span>
              {l === 'vanilla' ? t('create.loader.none') : t('loader.' + l)}
            </button>
          {/each}
        </div>
        {#if loader !== 'vanilla'}
          <label class="field">
            <span class="label">{t('create.loaderVersion')}</span>
            {#if loaderState === 'loading'}
              <div class="bar indeterminate"><span></span></div>
            {:else if loaderState === 'none'}
              <p class="hint warn">{t('create.loader.unavailable', { loader: t('loader.' + loader) })}</p>
            {:else if loaderState === 'error'}
              <p class="hint warn">{t('error.network.timeout')}</p>
            {:else}
              <select class="input" bind:value={loaderVersion}>
                {#each loaderVersions as lv (lv.version)}
                  <option value={lv.version}>
                    {lv.version}{lv.label === 'recommended' ? ` · ${t('create.recommended')}` : lv.label === 'latest' ? ` · ${t('create.latest')}` : !lv.stable ? ` · ${t('create.unstable')}` : ''}
                  </option>
                {/each}
              </select>
            {/if}
          </label>
        {/if}

        <h3 class="t-label section">{t('create.step.name')}</h3>
        <label class="field">
          <span class="label">{t('create.name')}</span>
          <input class="input" bind:value={name} oninput={() => (nameTouched = true)} maxlength="80" />
          <span class="hint" class:warn={nameProblem && nameProblem !== 'empty'}>
            {nameProblem === 'invalid' ? t('error.instance.invalidName') : nameProblem === 'exists' ? t('error.instance.exists', { id: name.trim() }) : t('create.name.hint')}
          </span>
        </label>
        <div class="field">
          <span class="label">{t('instances.color')}</span>
          <div class="swatches">
            {#each palette as c (c)}
              <button class="swatch" style:background={c} aria-pressed={color === c} aria-label={c} onclick={() => (color = c)}></button>
            {/each}
          </div>
        </div>
        <div class="toggle-row">
          <div><span>{t('instances.isolation')}</span><p class="hint">{t('instances.isolation.hint')}</p></div>
          <button class="switch" role="switch" aria-checked={isolated} aria-label={t('instances.isolation')} onclick={() => (isolated = !isolated)}></button>
        </div>
      </section>
    </div>
  {/if}

  {#if modpack}
    <label class="field">
      <span class="label">{t('create.name')}</span>
      <input class="input" bind:value={name} maxlength="80" />
      {#if nameProblem && nameProblem !== 'empty'}<span class="hint warn">{nameProblem === 'exists' ? t('error.instance.exists', { id: name.trim() }) : t('error.instance.invalidName')}</span>{/if}
    </label>
  {/if}

  {#if error}<p class="error selectable">{error}</p>{/if}

  {#snippet footer()}
    {#if !modpack}<button class="btn ghost" style="margin-right:auto" onclick={pickModpack}><Icon name="archive" />{t('create.importMrpack')}</button>{/if}
    <button class="btn" onclick={() => { app.createOpen = false; reset(); }}>{t('common.cancel')}</button>
    <button class="btn primary" disabled={!canInstall} onclick={install}><Icon name="download" />{t('create.install')}</button>
  {/snippet}
</Dialog>

<style>
  .grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 28px; }
  .col { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
  .col-head { display: flex; justify-content: space-between; align-items: center; gap: 12px; }
  .section { margin-top: 8px; }
  .versions { height: 360px; overflow: auto; }
  .versions button { all: unset; box-sizing: border-box; width: 100%; display: flex; justify-content: space-between; padding: 9px 12px; cursor: pointer; border-bottom: 1px solid var(--rule-faint); font-variant-numeric: tabular-nums; }
  .versions button:hover { background: var(--panel-raised); }
  .versions button.sel { background: var(--accent-wash); box-shadow: inset 2px 0 0 var(--accent); }
  .versions button:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
  .vid { font-weight: 600; }
  .pad { padding: 12px; }
  .loaders { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px; }
  .loader { display: flex; align-items: center; gap: 10px; height: 42px; padding: 0 12px; border: 1px solid var(--rule-strong); background: transparent; cursor: pointer; font-size: 14px; text-align: left; }
  .loader:hover { background: var(--panel-raised); }
  .loader[aria-pressed='true'] { border-color: var(--accent); background: var(--accent-wash); font-weight: 600; }
  .hint { font-size: 12px; color: var(--muted); line-height: 1.5; }
  .warn { color: var(--caution); }
  .swatches { display: flex; gap: 6px; }
  .swatch { width: 28px; height: 28px; border: 2px solid transparent; cursor: pointer; padding: 0; outline-offset: 2px; }
  .swatch[aria-pressed='true'] { outline: 2px solid var(--ink); }
  .toggle-row { display: flex; justify-content: space-between; gap: 16px; align-items: flex-start; padding-top: 4px; }
  .error { color: var(--warning); font-size: 13px; }
  .modpack { display: grid; grid-template-columns: auto 1fr auto; gap: 14px; align-items: center; padding: 14px; }
</style>
