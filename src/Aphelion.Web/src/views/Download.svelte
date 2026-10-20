<script lang="ts">
  import { app, fail, refreshInstances, reportTask, selectedInstance } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { describeError, t } from '../lib/i18n/index.svelte';
  import { count, date, relative } from '../lib/format';
  import type { ManifestVersion, ModrinthHit, ModrinthProject, ModrinthSearch, ModrinthVersion, ProjectType, Task } from '../lib/types';
  import ViewHeader from '../components/ViewHeader.svelte';
  import Icon from '../components/Icon.svelte';
  import Markdown from '../components/Markdown.svelte';
  import { glide, rise, stagger } from '../lib/motion';

  const tabs = ['versions', 'mod', 'modpack', 'resourcePack', 'shader'] as const;
  type Tab = (typeof tabs)[number];
  let tab = $state<Tab>((tabs as readonly string[]).includes(app.downloadTab) ? (app.downloadTab as Tab) : 'versions');

  // ---- Game versions ----
  let versions = $state<ManifestVersion[]>([]);
  let versionFilter = $state<'release' | 'snapshot' | 'old'>('release');
  let versionQuery = $state('');
  let versionError = $state<string | null>(null);
  const shownVersions = $derived(versions.filter((v) =>
    (versionFilter === 'release' ? v.type === 'release' : versionFilter === 'snapshot' ? v.type === 'snapshot' : v.type.startsWith('old_')) &&
    (!versionQuery || v.id.includes(versionQuery.trim()))));

  async function loadVersions(refresh = false) {
    try {
      versionError = null;
      versions = (await call<{ versions: ManifestVersion[] }>('versions.list', { refresh })).versions;
    } catch (e) {
      versionError = describeError(e);
    }
  }

  // ---- Modrinth ----
  let targetId = $state<string | null>(app.settings?.selectedInstance ?? null);
  const target = $derived(app.instances.find((i) => i.id === targetId) ?? selectedInstance());
  let query = $state('');
  let sort = $state('relevance');
  let gameFilter = $state<string>('');
  let loaderFilter = $state<string>('');
  let results = $state<ModrinthHit[]>([]);
  let total = $state(0);
  let searching = $state(false);
  let searchError = $state<string | null>(null);
  let selected = $state<ModrinthHit | null>(null);
  let project = $state<ModrinthProject | null>(null);
  let projectVersions = $state<ModrinthVersion[] | null>(null);
  let versionId = $state<string>('');
  let installing = $state(false);
  let modpackName = $state('');

  const loaderSlug = (kind: string | undefined) =>
    kind === 'fabric' ? 'fabric' : kind === 'quilt' ? 'quilt' : kind === 'forge' || kind === 'legacyForge' ? 'forge' : kind === 'neoForge' ? 'neoforge' : '';

  // Filters follow the install target, so results fit the instance by default.
  $effect(() => {
    if (tab === 'versions' || tab === 'modpack') return;
    gameFilter = target?.gameVersion ?? '';
    loaderFilter = tab === 'mod' ? loaderSlug(target?.loader.kind) : '';
  });

  let timer: ReturnType<typeof setTimeout> | undefined;
  $effect(() => {
    if (tab === 'versions') {
      if (versions.length === 0) loadVersions();
      return;
    }
    const args = { query, type: tab as ProjectType, gameVersion: gameFilter || null, loader: loaderFilter || null, sort };
    clearTimeout(timer);
    timer = setTimeout(() => search(args, 0), query ? 300 : 0);
  });

  async function search(args: Record<string, unknown>, offset: number) {
    searching = true;
    searchError = null;
    try {
      const r = await call<ModrinthSearch>('modrinth.search', { ...args, offset, limit: 24 });
      results = offset === 0 ? r.hits : [...results, ...r.hits];
      total = r.total_hits;
      if (offset === 0) select(r.hits[0] ?? null);
    } catch (e) {
      searchError = describeError(e);
      if (offset === 0) { results = []; total = 0; select(null); }
    } finally {
      searching = false;
    }
  }

  async function select(hit: ModrinthHit | null) {
    selected = hit;
    project = null;
    projectVersions = null;
    versionId = '';
    modpackName = hit?.title.replace(/[\\/:*?"<>|]/g, ' ').trim() ?? '';
    if (!hit) return;
    const id = hit.project_id;
    try {
      const [p, v] = await Promise.all([
        call<ModrinthProject>('modrinth.project', { id }),
        call<ModrinthVersion[]>('modrinth.versions', { id, gameVersion: tab === 'modpack' ? null : gameFilter || null, loader: tab === 'mod' ? loaderFilter || null : null }),
      ]);
      if (selected?.project_id !== id) return;
      project = p;
      projectVersions = v;
      versionId = (v.find((x) => x.version_type === 'release') ?? v[0])?.id ?? '';
    } catch (e) {
      if (selected?.project_id === id) projectVersions = [];
      fail(e);
    }
  }

  async function install() {
    if (!selected) return;
    installing = true;
    try {
      if (tab === 'modpack') {
        const id = await call<string>('modpack.install', { id: modpackName.trim(), displayName: selected.title, versionId });
        reportTask(id, () => { refreshInstances(); return t('create.done', { name: modpackName }); });
      } else if (target) {
        const id = await call<string>('content.install', { instanceId: target.id, type: tab, projectId: selected.project_id, versionId: versionId || null, title: selected.title });
        reportTask(id, (task: Task) => t('download.installed', { name: (task.result as string[] | null)?.join(', ') ?? selected!.title }));
      }
    } catch (e) {
      fail(e);
    } finally {
      installing = false;
    }
  }

  const tabLabel = (x: Tab) => t('download.tab.' + x);
  const noLoaderTarget = $derived(tab === 'mod' && target?.loader.kind === 'vanilla');
</script>

<ViewHeader title={t('download.title')} subtitle={tab === 'versions' ? t('download.versionsList.title') : t('download.source')} />
<div class="tabs" role="tablist" aria-label={t('download.title')} use:glide>
  {#each tabs as x (x)}
    <button role="tab" aria-selected={tab === x} onclick={() => { tab = x; app.downloadTab = x; }}>{tabLabel(x)}</button>
  {/each}
</div>

{#if tab === 'versions'}
  <section class="versions">
    <div class="toolbar">
      <div class="seg">
        {#each ['release', 'snapshot', 'old'] as f (f)}
          <button aria-pressed={versionFilter === f} onclick={() => (versionFilter = f as typeof versionFilter)}>{t('create.filter.' + f)}</button>
        {/each}
      </div>
      <label class="search"><Icon name="search" size={14} /><input class="input" placeholder={t('create.searchVersion')} bind:value={versionQuery} /></label>
      <button class="btn sm" onclick={() => loadVersions(true)}><Icon name="refresh" size={14} />{t('common.refresh')}</button>
    </div>
    {#if versionError}<p class="empty">{versionError}</p>{/if}
    <ul class="vgrid">
      {#each shownVersions as v, n (v.id)}
        <li class="vrow" in:rise={{ delay: stagger(n, 12, 20), y: 4 }}>
          <span class="vid">{v.id}</span>
          <span class="muted tnum">{date(v.releaseTime)}</span>
          <button class="btn sm" onclick={() => { app.createVersion = v.id; app.createOpen = true; }}><Icon name="plus" size={14} />{t('instances.new')}</button>
        </li>
      {/each}
    </ul>
  </section>
{:else}
  <div class="browser">
    <section class="results">
      <div class="toolbar">
        <label class="search grow"><Icon name="search" size={14} /><input class="input" placeholder={t('download.search')} bind:value={query} /></label>
        {#if tab !== 'modpack'}
          <input class="input mini tnum" placeholder={t('download.anyVersion')} bind:value={gameFilter} aria-label={t('instances.overview.version')} />
        {/if}
        {#if tab === 'mod' || tab === 'modpack'}
          <select class="input mini" bind:value={loaderFilter} aria-label={t('instances.overview.loader')}>
            <option value="">{t('download.anyLoader')}</option>
            <option value="fabric">Fabric</option><option value="quilt">Quilt</option><option value="forge">Forge</option><option value="neoforge">NeoForge</option>
          </select>
        {/if}
        <select class="input mini" bind:value={sort} aria-label={t('download.sort.relevance')}>
          {#each ['relevance', 'downloads', 'follows', 'newest', 'updated'] as s (s)}<option value={s}>{t('download.sort.' + s)}</option>{/each}
        </select>
      </div>
      <p class="t-label count">{searching && results.length === 0 ? t('common.loading') : t('download.results', { n: count(total) })}</p>
      {#if searchError}
        <p class="empty">{t('download.offline')} {searchError}</p>
      {:else if !searching && results.length === 0}
        <p class="empty">{t('download.noResults')}</p>
      {/if}
      {#if searching && results.length === 0}
        <ul class="hits" aria-hidden="true">
          {#each Array(6) as _, n (n)}
            <li class="hit"><span class="noicon skeleton"></span><span class="meta"><span class="sk skeleton" style:width="{30 + ((n * 23) % 30)}%"></span><span class="sk skeleton"></span><span class="sk skeleton" style:width="60%"></span></span></li>
          {/each}
        </ul>
      {/if}
      <ul class="hits">
        {#each results as hit, n (hit.project_id)}
          <li in:rise={{ delay: stagger(n % 24), y: 6 }}>
            <button class="hit" class:sel={selected?.project_id === hit.project_id} onclick={() => select(hit)}>
              {#if hit.icon_url}<img src={hit.icon_url} alt="" width="48" height="48" loading="lazy" />{:else}<span class="noicon"><Icon name="cube" size={20} /></span>{/if}
              <span class="meta">
                <span class="title-line"><span class="title">{hit.title}</span><span class="author">{hit.author}</span></span>
                <span class="desc">{hit.description}</span>
              </span>
              <span class="stats tnum"><span>{t('download.downloads', { n: count(hit.downloads) })}</span><span>{relative(hit.date_modified)}</span></span>
            </button>
          </li>
        {/each}
      </ul>
      {#if results.length < total && results.length > 0}
        <button class="btn more" disabled={searching} onclick={() => search({ query, type: tab, gameVersion: gameFilter || null, loader: loaderFilter || null, sort }, results.length)}>{t('common.loadMore')}</button>
      {/if}
    </section>

    <aside class="detail" aria-label={t('common.details')}>
      {#if selected}
        {#key selected.project_id}
        <div class="detail-body" in:rise={{ y: 8 }}>
        <div class="head">
          {#if selected.icon_url}<img src={selected.icon_url} alt="" width="56" height="56" />{:else}<span class="noicon big"><Icon name="cube" size={24} /></span>{/if}
          <div class="titles">
            <span class="t-section">{selected.title}</span>
            <span class="t-label">{selected.author}{project?.license ? ' · ' + (project.license.name ?? project.license.id) : ''}</span>
          </div>
        </div>

        <div class="install">
          {#if tab === 'modpack'}
            <label class="field"><span class="label">{t('create.name')}</span><input class="input" bind:value={modpackName} maxlength="80" /></label>
          {:else}
            <label class="field">
              <span class="label">{t('download.installTo')}</span>
              {#if app.instances.length === 0}
                <span class="hint">{t('download.noInstance')}</span>
              {:else}
                <select class="input" value={target?.id} onchange={(e) => (targetId = (e.target as HTMLSelectElement).value)}>
                  {#each app.instances as i (i.id)}<option value={i.id}>{i.name} — {i.gameVersion} · {t('loader.' + i.loader.kind)}</option>{/each}
                </select>
              {/if}
            </label>
          {/if}
          <label class="field">
            <span class="label">{t('download.version')}</span>
            {#if projectVersions === null}
              <div class="bar indeterminate"><span></span></div>
            {:else if projectVersions.length === 0}
              <span class="hint warn">{t('download.noCompatible', { version: gameFilter || '—', loader: loaderFilter || '—' })}</span>
            {:else}
              <select class="input" bind:value={versionId}>
                {#each projectVersions.slice(0, 60) as v (v.id)}
                  <option value={v.id}>{v.version_number} · {v.version_type} · {v.game_versions.slice(-2).join(', ')} · {date(v.date_published)}</option>
                {/each}
              </select>
            {/if}
          </label>
          {#if tab === 'mod'}<p class="hint">{noLoaderTarget ? t('content.noLoader') : t('download.dependencies')}</p>{/if}
          <button class="btn primary lg" disabled={installing || !versionId || (tab !== 'modpack' && (!target || noLoaderTarget)) || (tab === 'modpack' && !modpackName.trim())} onclick={install}>
            <Icon name="download" />{tab === 'modpack' ? t('download.installModpack') : t('download.install', { name: target?.name ?? '' })}
          </button>
        </div>

        <div class="about">
          <p class="lead">{selected.description}</p>
          {#if project}
            <div class="links">
              <button class="btn sm ghost" onclick={() => call('app.openUrl', { url: `https://modrinth.com/project/${project!.slug}` })}><Icon name="external" size={14} />{t('download.modrinthPage')}</button>
              {#if project.source_url}<button class="btn sm ghost" onclick={() => call('app.openUrl', { url: project!.source_url })}><Icon name="link" size={14} />{t('download.source.code')}</button>{/if}
              {#if project.issues_url}<button class="btn sm ghost" onclick={() => call('app.openUrl', { url: project!.issues_url })}><Icon name="alert" size={14} />{t('download.issues')}</button>{/if}
            </div>
            <Markdown source={project.body} />
          {/if}
        </div>
        </div>
        {/key}
      {/if}
    </aside>
  </div>
{/if}

<style>
  .versions { flex: 1; min-height: 0; display: flex; flex-direction: column; padding: 16px 28px 0; gap: 12px; }
  .toolbar { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
  .toolbar .input { height: 38px; }
  .grow { flex: 1; min-width: 150px; }
  .mini { width: 116px; }
  .vgrid { overflow: auto; flex: 1; border-top: 1px solid var(--rule); }
  .vrow { display: grid; grid-template-columns: 180px 1fr auto; gap: 16px; align-items: center; padding: 8px 0; border-bottom: 1px solid var(--rule-faint); }
  .vid { font-weight: 700; font-variant-numeric: tabular-nums; }

  .browser { flex: 1; min-height: 0; display: grid; grid-template-columns: minmax(0, 1fr) 400px; }
  .results { padding: 16px 28px 16px; display: flex; flex-direction: column; gap: 10px; min-height: 0; overflow: auto; }
  .count { margin-top: 2px; }
  .hits { border-top: 1px solid var(--rule); }
  .hit { all: unset; box-sizing: border-box; width: 100%; cursor: pointer; display: grid; grid-template-columns: 48px minmax(0, 1fr) auto; gap: 14px; align-items: center; padding: 14px 12px; border-bottom: 1px solid var(--rule-faint); }
  .hit:hover { background: var(--panel-raised); }
  .hit.sel { background: var(--panel-raised); box-shadow: inset 2px 0 0 var(--accent); }
  .hit:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
  .hit img, .head img { object-fit: cover; background: var(--screen); }
  .noicon { width: 48px; height: 48px; display: grid; place-items: center; background: var(--panel-raised); color: var(--muted); }
  .noicon.big { width: 56px; height: 56px; }
  .meta { display: flex; flex-direction: column; gap: 5px; min-width: 0; }
  .title-line { display: flex; gap: 10px; align-items: baseline; min-width: 0; }
  .title { font-size: 15px; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .author { font-size: 12px; color: var(--muted); white-space: nowrap; }
  .desc { font-size: 13px; color: var(--ink-3); line-height: 1.5; display: -webkit-box; -webkit-line-clamp: 2; line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; }
  .stats { display: flex; flex-direction: column; align-items: flex-end; gap: 4px; font-size: 12px; color: var(--muted); white-space: nowrap; }
  .more { align-self: center; margin: 8px 0; }

  .detail { border-left: 1px solid var(--rule); background: var(--panel-2); padding: 24px; display: flex; flex-direction: column; gap: 18px; overflow: auto; min-height: 0; }
  .detail-body { display: flex; flex-direction: column; gap: 18px; }
  .sk { display: block; height: 10px; width: 90%; }
  .hit img { transition: transform var(--t-base) var(--ease); }
  .hit:hover img { transform: scale(1.06); }
  .head { display: grid; grid-template-columns: 56px 1fr; gap: 14px; align-items: center; }
  .titles { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .install { display: flex; flex-direction: column; gap: 12px; padding-bottom: 18px; border-bottom: 1px solid var(--rule); }
  .hint { font-size: 12px; color: var(--muted); }
  .warn { color: var(--caution); }
  .about { display: flex; flex-direction: column; gap: 12px; }
  .lead { font-size: 14px; line-height: 1.6; }
  .links { display: flex; gap: 4px; flex-wrap: wrap; margin-left: -12px; }
</style>
