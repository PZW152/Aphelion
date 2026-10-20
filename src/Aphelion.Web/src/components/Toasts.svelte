<script lang="ts">
  import { flip } from 'svelte/animate';
  import { app, dismissToast, pauseToast, resumeToast } from '../lib/app.svelte';
  import { t } from '../lib/i18n/index.svelte';
  import { reduced, slide } from '../lib/motion';
  import Icon from './Icon.svelte';
</script>

<div class="toasts" aria-live="polite">
  {#each app.toasts as toast (toast.id)}
    <div
      class="toast {toast.kind}"
      role={toast.kind === 'error' ? 'alert' : 'status'}
      in:slide={{ x: 24 }}
      out:slide={{ x: 24, duration: 160 }}
      animate:flip={{ duration: reduced() ? 0 : 220 }}
      onpointerenter={() => pauseToast(toast.id)}
      onpointerleave={() => resumeToast(toast.id)}
    >
      <span class="lamp power-on {toast.kind === 'error' ? 'warning' : toast.kind === 'success' ? 'ok' : 'accent'}"></span>
      <p class="selectable">{toast.text}</p>
      <button class="btn sm icon" onclick={() => dismissToast(toast.id)} aria-label={t('common.dismiss')}><Icon name="close" size={12} /></button>
      <!-- Remaining time before the message dismisses itself; pauses while hovered. -->
      <span class="life" style:animation-duration="{toast.ms}ms"></span>
    </div>
  {/each}
</div>

<style>
  .toasts { position: fixed; right: 20px; bottom: 20px; display: flex; flex-direction: column; gap: 8px; z-index: 100; width: min(420px, calc(100vw - 40px)); }
  .toast {
    position: relative; overflow: hidden;
    display: grid; grid-template-columns: 8px 1fr auto; gap: 12px; align-items: center; padding: 8px 8px 8px 14px;
    background: var(--panel-raised); border: 1px solid var(--rule-strong); box-shadow: 0 12px 32px rgba(0, 0, 0, 0.35);
  }
  .toast.error { border-color: color-mix(in oklab, var(--warning) 55%, var(--rule)); }
  p { font-size: 13px; line-height: 1.5; white-space: pre-line; }
  .life { position: absolute; left: 0; bottom: 0; height: 2px; width: 100%; background: var(--rule-strong); transform-origin: left; animation: life linear both; }
  .error .life { background: color-mix(in oklab, var(--warning) 60%, transparent); }
  .success .life { background: color-mix(in oklab, var(--nominal) 60%, transparent); }
  .toast:hover .life { animation-play-state: paused; }
  @keyframes life { to { transform: scaleX(0); } }
</style>
