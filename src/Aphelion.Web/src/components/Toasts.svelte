<script lang="ts">
  import { app, dismissToast } from '../lib/app.svelte';
  import { t } from '../lib/i18n/index.svelte';
  import Icon from './Icon.svelte';
</script>

<div class="toasts" aria-live="polite">
  {#each app.toasts as toast (toast.id)}
    <div class="toast {toast.kind}" role={toast.kind === 'error' ? 'alert' : 'status'}>
      <span class="lamp {toast.kind === 'error' ? 'warning' : toast.kind === 'success' ? 'ok' : 'accent'}"></span>
      <p class="selectable">{toast.text}</p>
      <button class="btn sm icon" onclick={() => dismissToast(toast.id)} aria-label={t('common.dismiss')}><Icon name="close" size={12} /></button>
    </div>
  {/each}
</div>

<style>
  .toasts { position: fixed; right: 20px; bottom: 20px; display: flex; flex-direction: column; gap: 8px; z-index: 100; width: min(420px, calc(100vw - 40px)); }
  .toast {
    display: grid; grid-template-columns: 8px 1fr auto; gap: 12px; align-items: center; padding: 8px 8px 8px 14px;
    background: var(--panel-raised); border: 1px solid var(--rule-strong); box-shadow: 0 12px 32px rgba(0, 0, 0, 0.35);
    animation: in 180ms var(--ease);
  }
  .toast.error { border-color: color-mix(in oklab, var(--warning) 55%, var(--rule)); }
  p { font-size: 13px; line-height: 1.5; white-space: pre-line; }
  @keyframes in { from { opacity: 0; transform: translateX(16px); } }
</style>
