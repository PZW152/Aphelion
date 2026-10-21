<script lang="ts">
  import { app, fail, launch, launchTask, liveSessions, refreshInstances, reportTask, selectInstance, toast } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { describeError, t } from '../lib/i18n/index.svelte';
  import { date, dateTime, playtime, relative } from '../lib/format';
  import type { ContentFolder, World } from '../lib/types';
  import ViewHeader from '../components/ViewHeader.svelte';
  import Icon from '../components/Icon.svelte';
  import Dialog from '../components/Dialog.svelte';
  import ContentList from './ContentList.svelte';
  import InstanceSettings from './InstanceSettings.svelte';
  import { flip } from 'svelte/animate';
  import { glide, reduced, rise, slide, stagger, ticker } from '../lib/motion';

  let query = $state('');
  let currentId = $state<string | null>(app.focusInstance ?? app.settings?.selectedInstance ?? null);
  let tab = $state(app.instanceTab);
  app.focusInstance = null;
  app.instanceTab = 'overview';

  const filtered = $derived(
    app.instances.filter((i) => {
      const q = query.trim().toLowerCase();
      return !q || i.name.toLowerCase().includes(q) || i.id.toLowerCase().includes(q) || (i.gameVersion ?? '').includes(q) || i.loader.kind.toLowerCase().includes(q);
    }),
  );
  const current = $derived(app.instances.find((i) => i.id === currentId) ?? filtered[0] ?? null);
  const running = $derived(current ? liveSessions().some((s) => s.instanceId === current.id) || !!launchTask(current.id) : false);

  const tabs = $derived([
    ['overview', t('instances.tab.overview'), null],
    ['mods', t('instances.tab.mods'), current?.loader.kind === 'vanilla' ? null : current?.modCount ?? null],
    ['resourcepacks', t('instances.tab.resourcepacks'), null],
    ['shaderpacks', t('instances.tab.shaderpacks'), null],
    ['saves', t('instances.tab.saves'), null],
    ['settings', t('instances.tab.settings'), null],
  ] as [string, string, number | null][]);

  let worlds = $state<World[]>([]);
  $effect(() => {
    if (tab === 'saves' && current) call<World[]>('instances.saves', { id: current.id }).then((w) => (worlds = w)).catch(fail);
  });

  let renameOpen = $state(false);
  let renameValue = $state('');
  let renameError = $state<string | null>(null);
  let deleteOpen = $state(false);

  // A tab's content arrives from the side the tab sits on.
  let tabDir = $state(0);
  function pickTab(key: string) {
    const order = tabs.map((x) => x[0]);
    tabDir = Math.sign(order.indexOf(key) - order.indexOf(tab));
    tab = key;
  }

  function pick(id: string) {
    tabDir = 0;
    currentId = id;
    selectInstance(id);
  }

  async function rename() {
    if (!current) return;
    try {
      const newId = await call<string>('instances.rename', { id: current.id, newId: renameValue });
      currentId = newId;
      renameOpen = false;
      await refreshInstances();
    } catch (e) {
      renameError = describeError(e);
    }
  }

  async function remove() {
    if (!current) return;
    try {
      await call('instances.delete', { id: current.id });
      deleteOpen = false;
      currentId = null;
      await refreshInstances();
    } catch (e) {
      fail(e);
    }
  }

  async function repair() {
    if (!current) return;
    try {
      const id = await call<string>('instances.repair', { id: current.id });
      reportTask(id, (task) => (Number(task.result) > 0 ? t('instances.repaired', { n: Number(task.result) }) : t('instances.repairedNone')));
    } catch (e) {
      fail(e);
    }
  }

  async function exportPack() {
    if (!current) return;
    try {
      const id = await call<string | null>('modpack.export', { id: current.id });
      if (id) reportTask(id, (task) => String(task.result));
    } catch (e) {
      fail(e);
    }
  }

  async function addRoot() {
    try {
      const path = await call<string | null>('roots.add', {});
      if (path) await refreshInstances();
    } catch (e) {
      fail(e);
    }
  }

  async function selectRoot(path: string) {
    await call('roots.select', { path }).catch(fail);
    currentId = null;
    await refreshInstances();
  }
</script>

<div class="split">
  <section class="list" aria-label={t('instances.title')}>
    <div class="list-head drag">
      <h1 class="t-title">{t('instances.title')}</h1>
      <button class="btn primary sm" onclick={() => (app.createOpen = true)}><Icon name="plus" size={14} />{t('instances.new')}</button>
    </div>
    <div class="tools">
      <label class="root-select">
        <span class="sr-only">{t('instances.root')}</span>
        <select class="input" value={app.roots.find((r) => r.active)?.path} onchange={(e) => {
          const v = (e.target as HTMLSelectElement).value;
          if (v === '__add') { (e.target as HTMLSelectElement).value = app.roots.find((r) => r.active)?.path ?? ''; addRoot(); } else selectRoot(v);
        }}>
          {#each app.roots as r (r.path)}
            <option value={r.path}>{r.name} — {r.path}{r.exists ? '' : ` (${t('instances.rootMissing')})`}</option>
          {/each}
          <option value="__add">{t('instances.addRoot')}</option>
        </select>
      </label>
      <label class="search">
        <Icon name="search" size={14} />
        <input class="input" placeholder={t('instances.search')} bind:value={query} />
      </label>
    </div>
    <ul class="items" use:glide={{ axis: 'y' }}>
      {#each filtered as i, n (i.id)}
        <li in:slide={{ delay: stagger(n) }} out:slide={{ duration: 140 }} animate:flip={{ duration: reduced() ? 0 : 240 }}>
          <button class="item" class:sel={current?.id === i.id} aria-current={current?.id === i.id ? 'true' : undefined} onclick={() => pick(i.id)}>
            <span class="patch" style:--c={i.color}>{i.name.slice(0, 1).toUpperCase()}</span>
            <span class="meta">
              <span class="iname ellipsis">{i.name}</span>
              <span class="sub ellipsis">
                {i.gameVersion ?? '?'} · {t('loader.' + i.loader.kind)}{i.loader.kind !== 'vanilla' && i.modCount ? ` · ${i.modCount} ${t('launch.mods')}` : ''}
              </span>
            </span>
            <span class="when tnum">
              {#if liveSessions().some((s) => s.instanceId === i.id)}<span class="lamp ok"></span>{:else if i.error}<span class="lamp warning"></span>{:else}{i.lastPlayed ? relative(i.lastPlayed) : ''}{/if}
            </span>
          </button>
        </li>
      {:else}
        <li class="empty">{query ? t('instances.noMatch', { q: query }) : app.instancesLoaded ? t('instances.empty') : t('common.loading')}</li>
      {/each}
    </ul>
  </section>

  <section class="detail">
    {#if current}
      <header class="detail-head drag">
        <div class="titles">
          {#key current.id}<h2 class="t-identity title ellipsis" title={current.name} in:ticker>{current.name}</h2>{/key}
          <span class="t-label ellipsis">versions\{current.id} · {current.gameVersion ?? '?'} · {t('loader.' + current.loader.kind)} {current.loader.version ?? ''}</span>
        </div>
        <div class="head-actions">
          {#if current.loader.kind !== 'vanilla'}<button class="btn export" onclick={exportPack} title={t('instances.export')}><Icon name="archive" size={14} /><span class="lbl">{t('instances.export')}</span></button>{/if}
          <button class="btn primary" disabled={running || !!current.error} onclick={() => launch(current!)}><Icon name="play" size={14} />{t('instances.launch')}</button>
        </div>
      </header>
      <div class="tabs" role="tablist" use:glide>
        {#each tabs as [key, label, n] (key)}
          <button role="tab" aria-selected={tab === key} onclick={() => pickTab(key)}>{label}{#if n != null}<span class="count">{n}</span>{/if}</button>
        {/each}
      </div>

      {#key current.id + tab}
      <div class="pane" in:slide={{ x: tabDir * 18, duration: 280 }}>
        {#if tab === 'overview'}
          <div class="overview">
            {#if current.error}<p class="broken"><Icon name="alert" />{t('launch.broken', { error: current.error })}</p>{/if}
            <dl class="telemetry">
              <dt>{t('instances.overview.version')}</dt><dd>Minecraft {current.gameVersion ?? t('common.unknown')}</dd>
              <dt>{t('instances.overview.loader')}</dt><dd>{t('loader.' + current.loader.kind)} {current.loader.version ?? ''}</dd>
              <dt>{t('instances.overview.directory')}</dt>
              <dd class="path"><span class="selectable t-mono">{current.gameDirectory}</span> <button class="btn sm" onclick={() => call('instances.openFolder', { id: current!.id }).catch(fail)}><Icon name="folder" size={14} />{t('common.openFolder')}</button></dd>
              <dt>{t('instances.isolation')}</dt><dd>{current.isolated ? t('common.enabled') : t('common.disabled')}</dd>
              <dt>{t('instances.overview.created')}</dt><dd>{date(current.created)}</dd>
              <dt>{t('instances.overview.playtime')}</dt><dd>{playtime(current.playSeconds)} · {t('launch.lastPlayed')} {relative(current.lastPlayed)}</dd>
            </dl>
            <div class="ops">
              <div class="op">
                <div><strong>{t('instances.repair')}</strong><p class="muted">{t('instances.repair.hint')}</p></div>
                <button class="btn" onclick={repair}><Icon name="wrench" />{t('instances.repair')}</button>
              </div>
              <div class="op">
                <div><strong>{t('common.rename')}</strong><p class="muted t-mono">{current.id}</p></div>
                <button class="btn" disabled={running} onclick={() => { renameValue = current!.id; renameError = null; renameOpen = true; }}>{t('common.rename')}</button>
              </div>
              <div class="op">
                <div><strong>{t('instances.delete')}</strong><p class="muted">{t('instances.delete.body')}</p></div>
                <button class="btn danger" disabled={running} onclick={() => (deleteOpen = true)}><Icon name="trash" />{t('common.delete')}</button>
              </div>
            </div>
          </div>
        {:else if tab === 'mods' || tab === 'resourcepacks' || tab === 'shaderpacks'}
          <ContentList instance={current} folder={tab as ContentFolder} />
        {:else if tab === 'saves'}
          {#if worlds.length === 0}
            <p class="empty">{t('saves.empty')}</p>
          {:else}
            <ul class="worlds">
              {#each worlds as w, n (w.folder)}
                <li class="world" in:rise={{ delay: stagger(n), y: 6 }}>
                  {#if w.iconDataUrl}<img src={w.iconDataUrl} alt="" width="48" height="48" />{:else}<span class="noicon"><Icon name="globeGrid" size={20} /></span>{/if}
                  <div class="meta">
                    <span class="iname">{w.name}</span>
                    <span class="sub">{w.gameMode ? t('saves.mode.' + w.gameMode) : ''}{w.hardcore ? ' · ' + t('saves.hardcore') : ''} · {dateTime(w.lastPlayed)}</span>
                  </div>
                  <button class="btn sm" onclick={() => call('instances.openFolder', { id: current!.id, sub: 'saves/' + w.folder }).catch(fail)}><Icon name="folder" size={14} />{t('saves.open')}</button>
                </li>
              {/each}
            </ul>
          {/if}
        {:else if tab === 'settings'}
          <InstanceSettings instance={current} />
        {/if}
      </div>
      {/key}
    {:else}
      <div class="drag head-spacer"></div>
      <div class="empty">
        <p>{t('instances.empty')}</p>
        <button class="btn primary" onclick={() => (app.createOpen = true)}><Icon name="plus" />{t('instances.new')}</button>
      </div>
    {/if}
  </section>
</div>

<Dialog bind:open={renameOpen} title={t('instances.rename.title')} width={460}>
  <label class="field">
    <span class="label">{t('instances.rename.label')}</span>
    <input class="input" bind:value={renameValue} maxlength="80" onkeydown={(e) => e.key === 'Enter' && rename()} />
  </label>
  {#if renameError}<p class="error">{renameError}</p>{/if}
  {#snippet footer()}
    <button class="btn" onclick={() => (renameOpen = false)}>{t('common.cancel')}</button>
    <button class="btn primary" onclick={rename}>{t('common.rename')}</button>
  {/snippet}
</Dialog>

<Dialog bind:open={deleteOpen} title={t('instances.delete.title', { name: current?.name ?? '' })} width={480}>
  <p>{t('instances.delete.body')}</p>
  {#snippet footer()}
    <button class="btn" onclick={() => (deleteOpen = false)}>{t('common.cancel')}</button>
    <button class="btn danger" onclick={remove}><Icon name="trash" />{t('instances.delete.confirm')}</button>
  {/snippet}
</Dialog>

<style>
  .split { flex: 1; min-height: 0; display: grid; grid-template-columns: clamp(248px, 28vw, 360px) minmax(0, 1fr); }
  .list { border-right: 1px solid var(--rule); display: flex; flex-direction: column; min-height: 0; }
  .list-head { height: var(--header-h); flex: none; display: flex; align-items: center; justify-content: space-between; padding: 0 20px 0 28px; border-bottom: 1px solid var(--rule); }
  .tools { padding: 10px 20px 10px 28px; border-bottom: 1px solid var(--rule); display: flex; flex-direction: column; gap: 8px; }
  .root-select select { height: 34px; font-size: 13px; }
  .tools .input { height: 36px; }
  .items { overflow: auto; flex: 1; }
  .item { all: unset; box-sizing: border-box; width: 100%; cursor: pointer; display: grid; grid-template-columns: 40px minmax(0, 1fr) auto; gap: 14px; align-items: center; padding: 13px 20px 13px 28px; border-bottom: 1px solid var(--rule-faint); }
  .item:hover { background: var(--panel-raised); }
  /* The selection is one bar that travels between rows (motion.ts › glide), as in the rail. */
  .items { position: relative; }
  .items :global(.glide-y) { left: 0; right: 0; top: 0; background: var(--panel-raised); box-shadow: inset 2px 0 0 var(--accent); z-index: 0; }
  .items li { position: relative; z-index: 1; }
  .item.sel:hover { background: transparent; }
  .patch { transition: background var(--t-base), transform var(--t-base) var(--ease-spring); }
  .item:hover .patch { background: color-mix(in oklab, var(--c) 12%, transparent); }
  .item.sel .patch { background: color-mix(in oklab, var(--c) 22%, transparent); transform: scale(1.06); }
  .item:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
  .patch { width: 40px; height: 40px; border-radius: 50%; border: 1.5px solid var(--c); color: var(--c); display: grid; place-items: center; font-weight: 800; font-size: 16px; }
  .meta { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .iname { font-size: 15px; font-weight: 600; }
  .sub { font-size: 12px; color: var(--muted); }
  .when { font-size: 12px; color: var(--muted); }

  .detail { display: flex; flex-direction: column; min-width: 0; min-height: 0; }
  .detail-head { height: var(--header-h); flex: none; display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 0 160px 0 28px; border-bottom: 1px solid var(--rule); }
  .head-spacer { height: var(--header-h); border-bottom: 1px solid var(--rule); }
  .titles { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .title { font-size: 26px; }
  .head-actions { display: flex; gap: 8px; flex: none; }
  .pane { flex: 1; min-height: 0; overflow: auto; }
  .overview { padding: 20px 28px; display: flex; flex-direction: column; gap: 24px; max-width: 880px; }
  .path { display: flex; align-items: center; gap: 12px; justify-content: space-between; }
  .path .t-mono { font-size: 12px; overflow-wrap: anywhere; }
  .ops { display: flex; flex-direction: column; border-top: 1px solid var(--rule); }
  .op { display: flex; justify-content: space-between; align-items: center; gap: 24px; padding: 14px 0; border-bottom: 1px solid var(--rule); }
  .op p { font-size: 13px; margin-top: 2px; max-width: 52em; }
  .broken { display: flex; gap: 8px; color: var(--warning); }
  .worlds { padding: 12px 28px; display: flex; flex-direction: column; }
  .world { display: grid; grid-template-columns: 48px minmax(0, 1fr) auto; gap: 14px; align-items: center; padding: 10px 0; border-bottom: 1px solid var(--rule-faint); }
  .world img { image-rendering: pixelated; }
  .noicon { width: 48px; height: 48px; display: grid; place-items: center; background: var(--screen); border: 1px solid var(--rule); color: var(--muted); }
  .error { color: var(--warning); font-size: 13px; }
  .tabs { overflow-x: auto; scrollbar-width: none; }
  /* Narrow windows: the export action keeps only its glyph, so the instance name keeps its room. */
  @media (max-width: 1040px) {
    .export .lbl { display: none; }
    .title { font-size: 22px; }
    .list-head, .tools, .item { padding-left: 20px; }
    .detail-head, .overview { padding-left: 22px; }
    .op { flex-wrap: wrap; gap: 10px 24px; }
  }
</style>
