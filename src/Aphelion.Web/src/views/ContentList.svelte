<script lang="ts">
  import { onMount } from 'svelte';
  import { app, fail, go, reportTask, toast } from '../lib/app.svelte';
  import { call, on, sendFiles, inHost } from '../lib/rpc';
  import { t } from '../lib/i18n/index.svelte';
  import { bytes } from '../lib/format';
  import type { ContentFolder, ContentUpdate, Instance, LocalContent } from '../lib/types';
  import Icon from '../components/Icon.svelte';
  import { flip } from 'svelte/animate';
  import { fade } from 'svelte/transition';
  import { hatch, reduced, rise, slide, stagger } from '../lib/motion';

  let { instance, folder }: { instance: Instance; folder: ContentFolder } = $props();

  let items = $state<LocalContent[] | null>(null);
  let updates = $state<ContentUpdate[] | null>(null);
  let checking = $state(false);
  let dragging = $state(false);
  let query = $state('');

  const context = $derived(`content:${instance.id}:${folder}`);
  const noLoader = $derived(folder === 'mods' && instance.loader.kind === 'vanilla');
  const enabled = $derived(items?.filter((i) => i.enabled).length ?? 0);
  const shown = $derived(
    (items ?? []).filter((i) => !query || `${i.name ?? ''} ${i.fileName} ${i.id ?? ''}`.toLowerCase().includes(query.toLowerCase())),
  );

  async function load() {
    try {
      items = await call<LocalContent[]>('content.list', { instanceId: instance.id, folder });
    } catch (e) {
      fail(e);
      items = [];
    }
  }

  onMount(() => {
    load();
    const offDrop = on('files.dropped', async (d: { context: string; paths: string[] }) => {
      if (d.context !== context) return;
      try {
        const added = await call<string[]>('content.import', { instanceId: instance.id, folder, paths: d.paths });
        toast(t('content.imported', { n: added.length }), 'success');
        load();
      } catch (e) {
        fail(e);
      }
    });
    const offChanged = on('content.changed', (d: { instanceId: string; folder: string }) => {
      if (d.instanceId === instance.id && d.folder === folder) load();
    });
    return () => { offDrop(); offChanged(); };
  });

  async function toggle(item: LocalContent) {
    try {
      await call('content.toggle', { instanceId: instance.id, folder, file: item.fileName, enabled: !item.enabled });
      await load();
    } catch (e) {
      fail(e);
    }
  }

  async function remove(item: LocalContent) {
    try {
      await call('content.remove', { instanceId: instance.id, folder, file: item.fileName });
      toast(t('content.removed', { name: item.name ?? item.fileName }));
      await load();
    } catch (e) {
      fail(e);
    }
  }

  async function checkUpdates() {
    checking = true;
    try {
      updates = await call<ContentUpdate[]>('content.checkUpdates', { instanceId: instance.id, folder });
      if (updates.length === 0) toast(t('content.noUpdates'), 'success');
    } catch (e) {
      fail(e);
    } finally {
      checking = false;
    }
  }

  async function update(u: ContentUpdate) {
    const type = folder === 'mods' ? 'mod' : folder === 'resourcepacks' ? 'resourcePack' : 'shader';
    try {
      const id = await call<string>('content.install', { instanceId: instance.id, type, projectId: u.projectId, versionId: u.latest.id, title: u.fileName });
      updates = updates?.filter((x) => x !== u) ?? null;
      reportTask(id, () => t('download.installed', { name: u.latest.version_number }));
    } catch (e) {
      fail(e);
    }
  }

  function browse() {
    app.downloadTab = folder === 'mods' ? 'mod' : folder === 'resourcepacks' ? 'resourcePack' : 'shader';
    go('download');
  }

  function onDrop(e: DragEvent) {
    e.preventDefault();
    dragging = false;
    if (e.dataTransfer?.files.length) sendFiles(context, e.dataTransfer.files);
  }
</script>

<div
  class="content"
  class:dragging
  role="region"
  aria-label={t('instances.tab.' + folder)}
  ondragover={(e) => { e.preventDefault(); if (inHost && !noLoader) dragging = true; }}
  ondragleave={(e) => { if (e.currentTarget === e.target) dragging = false; }}
  ondrop={onDrop}
>
  {#if noLoader}
    <p class="empty">{t('content.noLoader')}</p>
  {:else}
    <div class="toolbar">
      <span class="t-label">
        {#if folder === 'mods'}{t('content.summary', { enabled, disabled: (items?.length ?? 0) - enabled })}{:else}{items?.length ?? 0}{/if}
      </span>
      <div class="actions">
        <label class="search small">
          <Icon name="search" size={14} />
          <input class="input" placeholder={t('common.search')} bind:value={query} />
        </label>
        <button class="btn sm" disabled={checking || !items?.length} onclick={checkUpdates}><Icon name="refresh" size={14} />{t('content.checkUpdates')}</button>
        <button class="btn sm" onclick={() => call('instances.openFolder', { id: instance.id, sub: folder }).catch(fail)}><Icon name="folder" size={14} />{t('common.openFolder')}</button>
        <button class="btn sm primary" onclick={browse}><Icon name="plus" size={14} />{t('content.addFromModrinth')}</button>
      </div>
    </div>

    {#if updates && updates.length > 0}
      <div class="updates screen" transition:hatch>
        <span class="t-label">{t('content.updates', { n: updates.length })}</span>
        {#each updates as u (u.fileName)}
          <div class="update">
            <span class="ellipsis">{u.fileName}</span>
            <span class="t-mono muted">{u.currentVersion} → {u.latest.version_number}</span>
            <button class="btn sm" onclick={() => update(u)}>{t('content.update')}</button>
          </div>
        {/each}
      </div>
    {/if}

    {#if items === null}
      <ul class="screen list" aria-hidden="true">
        {#each Array(6) as _, n (n)}
          <li class="item"><span class="noicon skeleton"></span><span class="meta"><span class="sk-line skeleton" style:width="{40 + ((n * 37) % 35)}%"></span><span class="sk-line short skeleton"></span></span></li>
        {/each}
      </ul>
    {:else if items.length === 0}
      <p class="empty" in:rise>{t('content.empty.' + folder)}</p>
    {:else}
      <ul class="screen list">
        {#each shown as item, n (item.fileName.replace(/\.disabled$/i, ''))}
          <li class="item" class:off={!item.enabled} in:rise={{ delay: stagger(n), y: 6 }} out:slide={{ x: -24, duration: 180 }} animate:flip={{ duration: reduced() ? 0 : 240 }}>
            {#if item.iconDataUrl}<img src={item.iconDataUrl} alt="" width="36" height="36" />{:else}<span class="noicon"><Icon name={folder === 'mods' ? 'cube' : folder === 'shaderpacks' ? 'sun' : 'image'} size={18} /></span>{/if}
            <div class="meta">
              <span class="name ellipsis">{item.name ?? item.fileName}</span>
              <span class="desc ellipsis">{item.description ?? item.fileName}</span>
            </div>
            <span class="ver t-mono ellipsis" title={item.fileName}>{item.version ?? bytes(item.size)}</span>
            <button class="switch" role="switch" aria-checked={item.enabled} aria-label={t('content.toggle', { name: item.name ?? item.fileName })} onclick={() => toggle(item)}></button>
            <button class="btn sm icon" aria-label={t('common.remove')} onclick={() => remove(item)}><Icon name="trash" size={14} /></button>
          </li>
        {/each}
      </ul>
    {/if}
  {/if}
  {#if dragging}<div class="drop" transition:fade={{ duration: reduced() ? 0 : 140 }}><span>{t('content.dropHere', { name: instance.name })}</span></div>{/if}
</div>

<style>
  .content { position: relative; padding: 20px 28px; display: flex; flex-direction: column; gap: 14px; min-height: 100%; }
  .toolbar { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; }
  .actions { display: flex; gap: 8px; flex-wrap: wrap; }
  .search.small .input { height: 34px; width: 180px; }
  .list { display: flex; flex-direction: column; }
  .item { display: grid; grid-template-columns: 36px minmax(0, 1fr) 140px 40px 34px; gap: 14px; align-items: center; padding: 10px 14px; border-bottom: 1px solid var(--rule-faint); }
  .item:last-child { border-bottom: 0; }
  .item img { image-rendering: pixelated; }
  .noicon { width: 36px; height: 36px; display: grid; place-items: center; background: var(--panel-raised); color: var(--muted); }
  .item img, .item .noicon, .item .name, .item .desc { transition: opacity var(--t-base), color var(--t-base), filter var(--t-base); }
  .off img, .off .noicon { opacity: 0.4; filter: grayscale(1); }
  .off .name { color: var(--muted); }
  .item:hover { background: color-mix(in oklab, var(--panel-raised) 50%, transparent); }
  .sk-line { display: block; height: 10px; }
  .sk-line.short { width: 70%; height: 8px; opacity: 0.6; }
  .meta { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .name { font-size: 14px; font-weight: 600; }
  .desc { font-size: 12px; color: var(--muted); }
  .ver { font-size: 12px; color: var(--muted); }
  .updates { padding: 12px 14px; display: flex; flex-direction: column; gap: 8px; }
  .update { display: grid; grid-template-columns: minmax(0, 1fr) auto auto; gap: 12px; align-items: center; font-size: 13px; }
  .drop { position: absolute; inset: 8px; border: 2px dashed var(--accent); animation: drop-pulse 1.2s var(--ease) infinite; background: color-mix(in oklab, var(--panel) 80%, transparent); display: grid; place-items: center; font-size: 16px; font-weight: 700; pointer-events: none; }
  @keyframes drop-pulse { 50% { border-color: color-mix(in oklab, var(--accent) 40%, transparent); } }
</style>
