<script lang="ts">
  import { app } from '../lib/app.svelte';
  import { call, inHost } from '../lib/rpc';
  import { t } from '../lib/i18n/index.svelte';
  import Icon from './Icon.svelte';
</script>

{#if inHost}
  <div class="controls no-drag">
    <button onclick={() => call('window.minimize')} aria-label={t('window.minimize')}><Icon name="minimize" size={12} /></button>
    <button onclick={() => call('window.toggleMaximize')} aria-label={t(app.maximized ? 'window.restore' : 'window.maximize')}>
      <Icon name={app.maximized ? 'restore' : 'maximize'} size={12} />
    </button>
    <button class="close" onclick={() => call('window.close')} aria-label={t('window.close')}><Icon name="close" size={12} /></button>
  </div>
{/if}

<style>
  .controls { position: fixed; top: 0; right: 0; display: flex; z-index: 50; }
  button { width: 46px; height: 32px; border: 0; background: transparent; color: var(--muted); display: grid; place-items: center; cursor: pointer; }
  button:hover { background: var(--panel-raised); color: var(--ink); }
  .close:hover { background: #c42b1c; color: #fff; }
</style>
