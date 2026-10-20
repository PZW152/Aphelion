<script lang="ts">
  import { onDestroy } from 'svelte';
  import { app, fail, go, refreshAccounts, toast } from '../lib/app.svelte';
  import { call } from '../lib/rpc';
  import { describeError, t } from '../lib/i18n/index.svelte';
  import { relative } from '../lib/format';
  import type { Account } from '../lib/types';
  import ViewHeader from '../components/ViewHeader.svelte';
  import PlayerHead from '../components/PlayerHead.svelte';
  import Dialog from '../components/Dialog.svelte';
  import Icon from '../components/Icon.svelte';

  let removing = $state<Account | null>(null);

  // Offline
  let offlineOpen = $state(false);
  let offlineName = $state('');
  let offlineError = $state<string | null>(null);
  const offlineValid = $derived(/^[A-Za-z0-9_]{3,16}$/.test(offlineName));

  // Microsoft
  let msOpen = $state(false);
  type DeviceFlow = { flowId: string; userCode: string; verificationUri: string; expiresAt: string };
  let msFlow = $state<DeviceFlow | null>(null);
  let msError = $state<string | null>(null);
  let now = $state(Date.now());
  const ticker = setInterval(() => (now = Date.now()), 1000);
  onDestroy(() => clearInterval(ticker));

  // Yggdrasil
  let yggOpen = $state(false);
  let ygg = $state({ server: '', username: '', password: '' });
  let yggBusy = $state(false);
  let yggError = $state<string | null>(null);
  let yggChoice = $state<{ choiceId: string; profiles: { id: string; name: string }[] } | null>(null);

  $effect(() => {
    if (app.accountDialog === 'menu') app.accountDialog = null; // the page itself is the menu
  });

  async function use(a: Account) {
    await call('accounts.select', { id: a.id }).catch(fail);
    await refreshAccounts();
  }

  async function remove() {
    if (!removing) return;
    await call('accounts.remove', { id: removing.id }).catch(fail);
    removing = null;
    await refreshAccounts();
  }

  async function addOffline() {
    try {
      await call('accounts.addOffline', { name: offlineName });
      offlineOpen = false;
      toast(t('accounts.signedIn', { name: offlineName }), 'success');
      offlineName = '';
      await refreshAccounts();
    } catch (e) {
      offlineError = describeError(e);
    }
  }

  async function startMicrosoft() {
    msError = null;
    msFlow = null;
    msOpen = true;
    try {
      const flow = await call<DeviceFlow>('accounts.beginMicrosoft');
      msFlow = flow;
      await call('accounts.openVerification', { url: flow.verificationUri }).catch(() => {});
      await call<string>('accounts.completeMicrosoft', { flowId: flow.flowId });
      msOpen = false;
      await refreshAccounts();
      toast(t('accounts.signedIn', { name: app.accounts.accounts.find((a) => a.id === app.accounts.selected)?.name ?? '' }), 'success');
    } catch (e) {
      if (msOpen) msError = describeError(e);
    }
  }

  function cancelMicrosoft() {
    if (msFlow) call('accounts.cancelMicrosoft', { flowId: msFlow.flowId });
    msOpen = false;
    msFlow = null;
  }

  async function signInYggdrasil() {
    yggBusy = true;
    yggError = null;
    try {
      const r = await call<{ accountId?: string; choiceId?: string; profiles?: { id: string; name: string }[] }>('accounts.addYggdrasil', ygg);
      if (r.choiceId) yggChoice = { choiceId: r.choiceId, profiles: r.profiles ?? [] };
      else finishYggdrasil();
    } catch (e) {
      yggError = describeError(e);
    } finally {
      yggBusy = false;
      ygg.password = '';
    }
  }

  async function chooseProfile(profileId: string) {
    try {
      await call('accounts.chooseProfile', { choiceId: yggChoice!.choiceId, profileId });
      finishYggdrasil();
    } catch (e) {
      yggError = describeError(e);
    }
  }

  async function finishYggdrasil() {
    yggOpen = false;
    yggChoice = null;
    await refreshAccounts();
  }

  function copyCode() {
    if (msFlow) navigator.clipboard.writeText(msFlow.userCode).then(() => toast(t('common.copied')));
  }

  const remaining = $derived(msFlow ? Math.max(0, Math.round((new Date(msFlow.expiresAt).getTime() - now) / 1000)) : 0);
</script>

<ViewHeader title={t('accounts.title')} />

<div class="page">
  <section class="add" aria-label={t('accounts.add')}>
    <button class="option" onclick={() => (app.accounts.microsoftAvailable ? startMicrosoft() : (msOpen = true))}>
      <span class="icon"><Icon name="key" size={20} /></span>
      <span class="text"><strong>{t('accounts.kind.microsoft')}</strong><span>{t('accounts.ms.start')}</span></span>
    </button>
    <button class="option" onclick={() => { offlineError = null; offlineOpen = true; }}>
      <span class="icon"><Icon name="user" size={20} /></span>
      <span class="text"><strong>{t('accounts.kind.offline')}</strong><span>{t('accounts.status.offline')}</span></span>
    </button>
    <button class="option" onclick={() => { yggError = null; yggChoice = null; yggOpen = true; }}>
      <span class="icon"><Icon name="globe" size={20} /></span>
      <span class="text"><strong>{t('accounts.kind.yggdrasil')}</strong><span>LittleSkin · Blessing Skin</span></span>
    </button>
  </section>

  {#if app.accounts.accounts.length === 0}
    <p class="empty">{t('accounts.empty')}</p>
  {:else}
    <ul class="cards">
      {#each app.accounts.accounts as a (a.id)}
        {@const current = a.id === app.accounts.selected}
        <li class="card" class:current>
          <PlayerHead skinUrl={a.skinUrl} uuid={a.uuid} size={64} />
          <div class="info">
            <span class="name">{a.name}</span>
            <span class="kind">{t('accounts.kind.' + a.kind)}{a.serverName ? ' · ' + a.serverName : ''}</span>
            <span class="state" class:bad={a.needsSignIn}>
              <span class="lamp {a.needsSignIn ? 'warning' : a.kind === 'offline' ? 'hollow' : 'ok'}"></span>
              {a.needsSignIn ? t('accounts.status.relogin') : a.kind === 'offline' ? t('accounts.status.offline') : t('accounts.status.ok')}
            </span>
            <span class="t-label t-mono uuid selectable">{a.uuid}</span>
            <span class="t-label">{t('launch.lastPlayed')} {relative(a.lastUsed)}</span>
          </div>
          <div class="actions">
            {#if current}<span class="chip"><span class="lamp accent"></span>{t('accounts.current')}</span>{:else}<button class="btn sm" onclick={() => use(a)}>{t('accounts.use')}</button>{/if}
            {#if a.needsSignIn && a.kind === 'microsoft'}<button class="btn sm primary" onclick={startMicrosoft}>{t('accounts.ygg.signIn')}</button>{/if}
            {#if a.needsSignIn && a.kind === 'yggdrasil'}<button class="btn sm primary" onclick={() => { ygg.server = a.server ?? ''; yggOpen = true; }}>{t('accounts.ygg.signIn')}</button>{/if}
            <button class="btn sm icon" aria-label={t('common.remove')} onclick={() => (removing = a)}><Icon name="trash" size={14} /></button>
          </div>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<Dialog bind:open={offlineOpen} title={t('accounts.offline.add')} width={440}>
  <label class="field">
    <span class="label">{t('accounts.offline.name')}</span>
    <input class="input" bind:value={offlineName} maxlength="16" spellcheck="false" onkeydown={(e) => e.key === 'Enter' && offlineValid && addOffline()} />
    <span class="hint" class:warn={offlineName.length > 0 && !offlineValid}>{t('accounts.offline.hint')}</span>
  </label>
  {#if offlineError}<p class="error">{offlineError}</p>{/if}
  {#snippet footer()}
    <button class="btn" onclick={() => (offlineOpen = false)}>{t('common.cancel')}</button>
    <button class="btn primary" disabled={!offlineValid} onclick={addOffline}>{t('common.add')}</button>
  {/snippet}
</Dialog>

<Dialog bind:open={msOpen} title={t('accounts.ms.start')} width={520} onclose={cancelMicrosoft}>
  {#if !app.accounts.microsoftAvailable}
    <p>{t('accounts.ms.noClientId')}</p>
  {:else if msError}
    <p class="error">{msError}</p>
  {:else if !msFlow}
    <div class="bar indeterminate"><span></span></div>
  {:else}
    <ol class="steps">
      <li><span class="n">01</span>{t('accounts.ms.step1', { uri: msFlow.verificationUri })}</li>
      <li><span class="n">02</span>{t('accounts.ms.step2')}</li>
    </ol>
    <button class="code screen" onclick={copyCode} title={t('common.copy')}>
      <span class="t-label">{t('accounts.ms.code')}</span>
      <span class="value t-mono selectable">{msFlow.userCode}</span>
      <Icon name="copy" />
    </button>
    <p class="waiting"><span class="lamp caution pulse"></span>{t('accounts.ms.waiting')} <span class="muted tnum">{t('accounts.ms.expires', { t: `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, '0')}` })}</span></p>
  {/if}
  {#snippet footer()}
    {#if !app.accounts.microsoftAvailable}
      <button class="btn" onclick={() => (msOpen = false)}>{t('common.close')}</button>
      <button class="btn primary" onclick={() => { msOpen = false; go('settings'); }}>{t('accounts.ms.setup')}</button>
    {:else}
      {#if msFlow}<button class="btn" onclick={() => call('accounts.openVerification', { url: msFlow!.verificationUri })}><Icon name="external" />{t('accounts.ms.open')}</button>{/if}
      {#if msError}<button class="btn primary" onclick={startMicrosoft}>{t('common.retry')}</button>{/if}
      <button class="btn" onclick={cancelMicrosoft}>{t('common.cancel')}</button>
    {/if}
  {/snippet}
</Dialog>

<Dialog bind:open={yggOpen} title={t('accounts.kind.yggdrasil')} width={480}>
  {#if yggChoice}
    <p class="t-label">{t('accounts.ygg.pickProfile')}</p>
    <ul class="profiles">
      {#each yggChoice.profiles as p (p.id)}
        <li><button class="btn" onclick={() => chooseProfile(p.id)}><PlayerHead uuid={p.id} size={24} />{p.name}</button></li>
      {/each}
    </ul>
  {:else}
    <form class="ygg" onsubmit={(e) => { e.preventDefault(); signInYggdrasil(); }}>
      <label class="field"><span class="label">{t('accounts.ygg.server')}</span><input class="input" bind:value={ygg.server} placeholder={t('accounts.ygg.server.placeholder')} spellcheck="false" required /></label>
      <label class="field"><span class="label">{t('accounts.ygg.username')}</span><input class="input" bind:value={ygg.username} autocomplete="username" spellcheck="false" required /></label>
      <label class="field">
        <span class="label">{t('accounts.ygg.password')}</span>
        <input class="input" type="password" bind:value={ygg.password} autocomplete="current-password" required />
        <span class="hint">{t('accounts.ygg.passwordHint')}</span>
      </label>
      <button type="submit" hidden aria-label={t('accounts.ygg.signIn')}></button>
    </form>
  {/if}
  {#if yggError}<p class="error">{yggError}</p>{/if}
  {#snippet footer()}
    <button class="btn" onclick={() => (yggOpen = false)}>{t('common.cancel')}</button>
    {#if !yggChoice}<button class="btn primary" disabled={yggBusy || !ygg.server || !ygg.username || !ygg.password} onclick={signInYggdrasil}>{t('accounts.ygg.signIn')}</button>{/if}
  {/snippet}
</Dialog>

<Dialog open={!!removing} title={t('accounts.remove.title', { name: removing?.name ?? '' })} width={440} onclose={() => (removing = null)}>
  <p>{t('accounts.remove.body')}</p>
  {#snippet footer()}
    <button class="btn" onclick={() => (removing = null)}>{t('common.cancel')}</button>
    <button class="btn danger" onclick={remove}>{t('common.remove')}</button>
  {/snippet}
</Dialog>

<style>
  .page { flex: 1; overflow: auto; padding: 24px 28px; display: flex; flex-direction: column; gap: 24px; }
  .add { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; max-width: 980px; }
  .option { display: grid; grid-template-columns: 44px 1fr; gap: 14px; align-items: center; padding: 14px; border: 1px solid var(--rule-strong); background: transparent; cursor: pointer; text-align: left; }
  .option:hover { background: var(--panel-raised); border-color: var(--muted); }
  .option .icon { width: 44px; height: 44px; display: grid; place-items: center; background: var(--screen); border: 1px solid var(--rule); color: var(--accent-ink); }
  .option .text { display: flex; flex-direction: column; gap: 2px; font-size: 12px; color: var(--muted); }
  .option strong { font-size: 15px; color: var(--ink); }
  .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(420px, 1fr)); gap: 8px; max-width: 1200px; }
  .card { display: grid; grid-template-columns: 64px minmax(0, 1fr) auto; gap: 18px; padding: 18px; background: var(--screen); border: 1px solid var(--rule); align-items: start; }
  .card.current { border-color: color-mix(in oklab, var(--accent) 60%, var(--rule)); box-shadow: inset 2px 0 0 var(--accent); }
  .info { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
  .name { font-size: 20px; font-weight: 800; font-stretch: 115%; }
  .kind { font-size: 13px; color: var(--ink-3); }
  .state { font-size: 12px; color: var(--muted); display: flex; align-items: center; gap: 6px; margin: 4px 0; }
  .state.bad { color: var(--warning); }
  .uuid { overflow: hidden; text-overflow: ellipsis; }
  .actions { display: flex; flex-direction: column; align-items: flex-end; gap: 6px; }
  .hint { font-size: 12px; color: var(--muted); }
  .warn { color: var(--caution); }
  .error { color: var(--warning); font-size: 13px; white-space: pre-line; }
  .steps { display: flex; flex-direction: column; gap: 8px; }
  .steps li { display: flex; gap: 12px; }
  .n { font-family: var(--font-mono); color: var(--accent-ink); }
  .code { display: grid; grid-template-columns: 1fr auto; grid-template-rows: auto auto; gap: 4px 12px; align-items: center; padding: 16px; cursor: pointer; text-align: left; }
  .code .value { font-size: 32px; font-weight: 600; letter-spacing: 0.12em; grid-row: 2; }
  .code :global(svg) { grid-row: 1 / 3; grid-column: 2; color: var(--muted); }
  .waiting { display: flex; align-items: center; gap: 10px; font-size: 13px; }
  .ygg { display: flex; flex-direction: column; gap: 14px; }
  .profiles { display: flex; flex-direction: column; gap: 6px; }
  .profiles .btn { width: 100%; justify-content: flex-start; }
  @media (max-width: 1100px) { .add { grid-template-columns: 1fr; } }
</style>
