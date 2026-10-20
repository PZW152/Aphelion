<script lang="ts">
  // Caution & warning panel: each lamp reports a real subsystem, never decoration.
  import { app, liveSessions, runningTasks, selectedAccount } from '../lib/app.svelte';
  import { t } from '../lib/i18n/index.svelte';
  import { megabytes } from '../lib/format';
  import { flicker } from '../lib/motion';

  type Tone = 'ok' | 'caution' | 'warning' | 'off' | 'plain';

  const cells = $derived.by(() => {
    const s = app.settings;
    const source = s?.download.source ?? 'auto';
    const account = selectedAccount();
    const tasks = runningTasks().length;
    const live = liveSessions();
    const starting = live.some((x) => x.state === 'starting');
    const list: { key: string; label: string; value: string; tone: Tone; secondary?: boolean }[] = [
      { key: 'net', label: t('ann.network'), value: t('ann.source.' + (source === 'mirror' ? 'mirror' : source === 'official' ? 'official' : 'auto')), tone: 'ok' },
      { key: 'java', label: t('ann.java'), value: app.java.length ? t('ann.java.count', { n: app.java.length }) : t('ann.java.none'), tone: app.java.length ? 'ok' : 'caution' },
      {
        key: 'auth', label: t('ann.account'),
        value: !account ? t('ann.account.none') : account.needsSignIn ? t('ann.account.relogin') : t('ann.account.ok'),
        tone: !account ? 'caution' : account.needsSignIn ? 'warning' : 'ok',
      },
      { key: 'tasks', label: t('ann.tasks'), value: tasks ? t('ann.tasks.running', { n: tasks }) : t('ann.tasks.idle'), tone: tasks ? 'caution' : 'off', secondary: true },
      { key: 'mem', label: t('ann.memory'), value: app.memory ? t('ann.memory.free', { v: megabytes(app.memory.availableMb) }) : '—', tone: app.memory && app.memory.availableMb < 2048 ? 'caution' : 'plain', secondary: true },
      {
        key: 'game', label: t('ann.game'),
        value: live.length === 0 ? t('ann.game.off') : starting ? t('ann.game.starting') : t('ann.game.running', { n: live.length }),
        tone: live.length === 0 ? 'off' : starting ? 'caution' : 'ok',
      },
    ];
    return list;
  });
</script>

<div class="panel" role="status" aria-label={t('ann.status')}>
  {#each cells as c, i (c.key)}
    <div class="cell annunciator-cell" class:secondary={c.secondary} style:--i={i}>
      <span class="t-legend">{c.label}</span>
      <!-- A changed reading re-lights like a lamp switching state (memory drifts constantly, so only its tone counts). -->
      {#key c.key === 'mem' ? c.tone : c.tone + c.value}
        <span class="value tnum {c.tone}" in:flicker>{c.value}</span>
      {/key}
    </div>
  {/each}
</div>

<style>
  .panel { display: grid; grid-template-columns: repeat(6, auto); gap: 4px; }
  .cell { padding: 6px 10px; background: var(--screen); border: 1px solid var(--rule); display: flex; flex-direction: column; gap: 2px; min-width: 66px; transition: border-color var(--t-base); }
  .cell:has(.warning) { border-color: color-mix(in oklab, var(--warning) 45%, var(--rule)); }
  .cell:has(.caution) { border-color: color-mix(in oklab, var(--caution) 35%, var(--rule)); }
  .value { font-size: 12px; font-weight: 700; white-space: nowrap; }
  .ok { color: var(--nominal); }
  .caution { color: var(--caution); }
  .warning { color: var(--warning); }
  .off { color: var(--off); }
  .plain { color: var(--ink); }
  /* Narrow windows keep one row: tasks and memory are also visible in the rail and settings. */
  @media (max-width: 1240px) { .panel { grid-template-columns: repeat(4, auto); } .secondary { display: none; } }
</style>
