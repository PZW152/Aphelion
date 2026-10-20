<script lang="ts">
  import { tick } from 'svelte';
  import { app, fail, toast } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { t } from '../lib/i18n/index.svelte';
  import { bytes, timespan } from '../lib/format';
  import type { LogLine } from '../lib/types';
  import ViewHeader from '../components/ViewHeader.svelte';
  import Icon from '../components/Icon.svelte';

  const RENDER_LIMIT = 2000;

  const session = $derived(app.sessions.find((s) => s.id === app.telemetrySession) ?? app.sessions[0] ?? null);
  const exit = $derived(session ? app.exits.find((e) => e.session.id === session.id)?.exit ?? null : null);
  let level = $state<'all' | 'warn' | 'error'>('all');
  let filter = $state('');
  let follow = $state(true);
  let logEl: HTMLDivElement | undefined = $state();

  // Lines received before this view opened are fetched once from the host.
  $effect(() => {
    const s = session;
    if (!s || (app.logs[s.id]?.length ?? 0) > 0) return;
    call<LogLine[]>('game.log', { sessionId: s.id, after: 0, limit: 8000 }).then((lines) => {
      if (!(app.logs[s.id]?.length)) app.logs[s.id] = lines;
    }).catch(() => {});
  });

  const all = $derived(session ? app.logs[session.id] ?? [] : []);
  const counts = $derived.by(() => {
    let i = 0, w = 0, e = 0;
    for (const l of all) {
      if (l.level === 'warn') w++;
      else if (l.level === 'error' || l.level === 'fatal') e++;
      else i++;
    }
    return { i, w, e };
  });
  const visible = $derived.by(() => {
    const q = filter.trim().toLowerCase();
    const lines = all.filter((l) =>
      (level === 'all' || (level === 'warn' ? l.level !== 'info' && l.level !== 'debug' && l.level !== 'trace' : l.level === 'error' || l.level === 'fatal')) &&
      (!q || l.message.toLowerCase().includes(q) || l.thread.toLowerCase().includes(q)));
    return lines.slice(-RENDER_LIMIT);
  });

  $effect(() => {
    void visible.length;
    if (follow && logEl) tick().then(() => logEl && (logEl.scrollTop = logEl.scrollHeight));
  });

  // Process memory history for the sparkline (sampled whenever the session list refreshes).
  let samples = $state<number[]>([]);
  let sampledFor = '';
  $effect(() => {
    if (!session) return;
    if (sampledFor !== session.id) { samples = []; sampledFor = session.id; }
    if (session.state !== 'exited' && session.memoryBytes > 0) samples = [...samples.slice(-59), session.memoryBytes];
  });
  const spark = $derived.by(() => {
    if (samples.length < 2) return '';
    const max = Math.max(...samples) * 1.1;
    return samples.map((v, i) => `${(i / (samples.length - 1)) * 240},${48 - (v / max) * 44}`).join(' ');
  });

  function time(iso: string) {
    const d = new Date(iso);
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}:${String(d.getSeconds()).padStart(2, '0')}`;
  }

  function copy() {
    const text = visible.map((l) => `[${time(l.time)}] [${l.thread}/${l.level.toUpperCase()}]: ${l.message}`).join('\n');
    navigator.clipboard.writeText(text).then(() => toast(t('common.copied')));
  }

  function onScroll() {
    if (!logEl) return;
    follow = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight < 40;
  }
</script>

<ViewHeader title={t('telemetry.title')} subtitle={session ? `${session.instanceName} · ${t('telemetry.state.' + session.state)}` : undefined}>
  {#if app.sessions.length > 1}
    <select class="input sessions" value={session?.id} onchange={(e) => (app.telemetrySession = (e.target as HTMLSelectElement).value)} aria-label={t('telemetry.sessions')}>
      {#each app.sessions as s (s.id)}<option value={s.id}>{s.instanceName} · {t('telemetry.state.' + s.state)} · PID {s.processId}</option>{/each}
    </select>
  {/if}
  {#if session && session.state !== 'exited'}
    <button class="btn" onclick={() => call('game.kill', { sessionId: session!.id }).catch(fail)}><Icon name="stop" size={14} />{t('launch.kill')}</button>
  {/if}
</ViewHeader>

{#if !session}
  <p class="empty">{t('telemetry.noSession')}</p>
{:else}
  <div class="layout">
    <div class="console-wrap">
      {#if exit?.crash}
        <section class="crash" aria-label={t('telemetry.crash.title')}>
          <header>
            <span class="lamp warning"></span>
            <h2 class="t-section">{t('telemetry.crash.title')}</h2>
            <span class="t-label tnum">{t('telemetry.exited', { code: exit.exitCode, t: timespan(exit.duration) })}</span>
          </header>
          <ol class="findings">
            {#each exit.crash.findings as f, i (f.code)}
              <li>
                <span class="n">{String(i + 1).padStart(2, '0')}</span>
                <div>
                  <p class="selectable">{t(f.code, f.args)}</p>
                  {#if f.evidence}<code class="selectable">{f.evidence}</code>{/if}
                </div>
              </li>
            {/each}
          </ol>
          {#if exit.crash.description}<p class="t-label">{t('telemetry.crash.description', { d: exit.crash.description })}</p>{/if}
          <div class="crash-actions">
            {#if exit.crash.crashReportPath}<button class="btn sm" onclick={() => call('app.revealFile', { path: exit!.crash!.crashReportPath }).catch(fail)}><Icon name="folder" size={14} />{t('telemetry.crash.report')}</button>{/if}
            {#if exit.crash.jvmCrashLogPath}<button class="btn sm" onclick={() => call('app.revealFile', { path: exit!.crash!.jvmCrashLogPath }).catch(fail)}><Icon name="folder" size={14} />{t('telemetry.crash.jvm')}</button>{/if}
          </div>
        </section>
      {:else if exit}
        <p class="exited t-label tnum"><span class="lamp hollow"></span>{t('telemetry.exited', { code: exit.exitCode, t: timespan(exit.duration) })}</p>
      {/if}

      <div class="console screen">
        <div class="bar-top">
          <span>{t('telemetry.output', { pid: session.processId })}</span>
          <div class="controls">
            <label class="search tiny"><Icon name="search" size={12} /><input class="input" placeholder={t('telemetry.filter')} bind:value={filter} /></label>
            <div class="seg small">
              {#each ['all', 'warn', 'error'] as l (l)}<button aria-pressed={level === l} onclick={() => (level = l as typeof level)}>{t('telemetry.level.' + l)}</button>{/each}
            </div>
            <span class="counts tnum"><span>{counts.i}</span><span class="w">{counts.w}</span><span class="e">{counts.e}</span></span>
            <button class="btn sm icon" onclick={copy} aria-label={t('telemetry.copyLog')} title={t('telemetry.copyLog')}><Icon name="copy" size={14} /></button>
          </div>
        </div>
        <div class="lines selectable" bind:this={logEl} onscroll={onScroll} role="log" aria-live="off">
          {#each visible as l (l.sequence)}
            <div class="line {l.level}"><span class="ts">[{time(l.time)}]</span> <span class="th">[{l.thread ? `${l.thread}/` : ''}{l.level.toUpperCase()}]</span> {l.message}</div>
          {/each}
        </div>
        {#if !follow}<button class="btn sm follow" onclick={() => { follow = true; logEl!.scrollTop = logEl!.scrollHeight; }}><Icon name="chevronDown" size={14} />{t('telemetry.follow')}</button>{/if}
      </div>
    </div>

    <aside class="side">
      <div class="gauge">
        <span class="t-label">{t('telemetry.processMemory')}</span>
        <span class="big tnum">{session.state === 'exited' ? '—' : bytes(session.memoryBytes)}</span>
        <svg viewBox="0 0 240 48" role="img" aria-label={t('telemetry.processMemory')}>
          <line x1="0" y1="4" x2="240" y2="4" stroke="var(--rule)" stroke-dasharray="3 4" />
          {#if spark}<polyline points={spark} fill="none" stroke="var(--accent)" stroke-width="1.5" />{/if}
        </svg>
      </div>
      <dl class="telemetry">
        <dt>PID</dt><dd class="t-mono">{session.processId}</dd>
        <dt>{t('ann.game')}</dt><dd>{t('telemetry.state.' + session.state)}</dd>
        <dt>{t('settings.afterLaunch')}</dt><dd>{t('settings.afterLaunch.' + (app.settings?.game.afterLaunch ?? 'keep'))}</dd>
      </dl>
    </aside>
  </div>
{/if}

<style>
  .sessions { height: 36px; width: 280px; }
  .layout { flex: 1; min-height: 0; display: grid; grid-template-columns: minmax(0, 1fr) 280px; }
  .console-wrap { display: flex; flex-direction: column; gap: 12px; padding: 20px 0 20px 28px; min-height: 0; }
  .console { flex: 1; min-height: 0; display: flex; flex-direction: column; position: relative; }
  .bar-top { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 6px 8px 6px 14px; border-bottom: 1px solid var(--rule); font-size: 12px; color: var(--muted); flex-wrap: wrap; }
  .controls { display: flex; gap: 8px; align-items: center; }
  .tiny .input { height: 30px; width: 180px; font-size: 12px; padding-left: 30px; }
  .tiny :global(svg) { left: 10px; }
  .seg.small > button { height: 30px; font-size: 12px; padding: 0 10px; }
  .counts { display: flex; gap: 10px; }
  .counts .w { color: var(--caution); }
  .counts .e { color: var(--warning); }
  .lines { flex: 1; overflow: auto; padding: 10px 14px; font-family: var(--font-mono); font-size: 12.5px; line-height: 1.7; color: var(--ink-3); white-space: pre-wrap; overflow-wrap: anywhere; }
  .line.warn { color: var(--caution); }
  .line.error, .line.fatal { color: var(--warning); }
  .line.debug, .line.trace { color: var(--faint); }
  .ts { color: var(--faint); }
  .th { color: var(--muted); }
  .follow { position: absolute; right: 16px; bottom: 12px; background: var(--panel-raised); }
  .exited { display: flex; align-items: center; gap: 8px; }

  .crash { border: 1px solid color-mix(in oklab, var(--warning) 50%, var(--rule)); background: color-mix(in oklab, var(--warning) 6%, var(--panel)); padding: 16px 18px; display: flex; flex-direction: column; gap: 12px; flex: none; max-height: 45%; overflow: auto; }
  .crash header { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
  .crash header .t-label { margin-left: auto; }
  .findings { display: flex; flex-direction: column; gap: 10px; }
  .findings li { display: grid; grid-template-columns: 28px 1fr; gap: 8px; }
  .findings p { white-space: pre-line; line-height: 1.6; }
  .findings code { display: block; margin-top: 4px; font-family: var(--font-mono); font-size: 12px; color: var(--muted); overflow-wrap: anywhere; }
  .n { font-family: var(--font-mono); color: var(--warning); }
  .crash-actions { display: flex; gap: 8px; }

  .side { padding: 20px 28px; display: flex; flex-direction: column; gap: 18px; }
  .gauge { display: flex; flex-direction: column; gap: 6px; }
  .big { font-size: 30px; font-weight: 800; font-stretch: 118%; }
  .gauge svg { width: 100%; height: 48px; }
</style>
