<script lang="ts">
  import type { Snippet } from 'svelte';
  import { t } from '../lib/i18n/index.svelte';
  import Icon from './Icon.svelte';
  import { reduced } from '../lib/motion';

  let {
    open = $bindable(false),
    title,
    width = 560,
    onclose,
    children,
    footer,
  }: { open?: boolean; title: string; width?: number; onclose?: () => void; children: Snippet; footer?: Snippet } = $props();

  let dialog: HTMLDialogElement;
  let closing = $state(false);

  // Opening plays a CSS hatch animation; closing plays it in reverse before the element is really closed.
  $effect(() => {
    if (open && !dialog.open) {
      closing = false;
      dialog.showModal();
    } else if (!open && dialog.open) {
      if (reduced()) { dialog.close(); return; }
      closing = true;
      setTimeout(() => {
        if (!open) dialog.close();
        closing = false;
      }, 170);
    }
  });

  function close() {
    open = false;
    onclose?.();
  }
</script>

<dialog bind:this={dialog} class:closing style:width="min({width}px, calc(100vw - 48px))" oncancel={(e) => { e.preventDefault(); close(); }} aria-labelledby="dialog-title">
  {#if open || closing}
    <header>
      <h2 id="dialog-title" class="t-section">{title}</h2>
      <button class="btn sm icon" onclick={close} aria-label={t('common.close')}><Icon name="close" size={14} /></button>
    </header>
    <div class="body">{@render children()}</div>
    {#if footer}<footer>{@render footer()}</footer>{/if}
  {/if}
</dialog>

<style>
  dialog {
    padding: 0; border: 1px solid var(--rule-strong); background: var(--panel); color: var(--ink);
    max-height: calc(100vh - 64px); display: none; flex-direction: column;
    box-shadow: 0 24px 64px rgba(0, 0, 0, 0.45);
  }
  dialog[open] { display: flex; animation: hatch-open 300ms var(--ease) both; }
  dialog.closing { animation: hatch-close 170ms var(--ease-in-out) both; }
  dialog::backdrop { background: rgba(5, 8, 10, 0.62); backdrop-filter: blur(2px); animation: veil 240ms var(--ease) both; }
  dialog.closing::backdrop { animation: veil 170ms var(--ease) reverse both; }
  /* Content settles a beat after the hatch has opened. */
  dialog[open] .body, dialog[open] header, dialog[open] footer { animation: settle 320ms var(--ease) both 90ms; }
  @keyframes hatch-open { from { clip-path: inset(48% 0 48% 0); opacity: 0.4; } to { clip-path: inset(0 0 0 0); opacity: 1; } }
  @keyframes hatch-close { from { clip-path: inset(0 0 0 0); opacity: 1; } to { clip-path: inset(48% 0 48% 0); opacity: 0; } }
  @keyframes veil { from { opacity: 0; } }
  @keyframes settle { from { opacity: 0; transform: translateY(6px); } }
  header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 18px 20px 14px 24px; border-bottom: 1px solid var(--rule); }
  .body { padding: 20px 24px; overflow: auto; display: flex; flex-direction: column; gap: 16px; min-height: 0; }
  footer { display: flex; justify-content: flex-end; gap: 8px; padding: 14px 24px 18px; border-top: 1px solid var(--rule); background: var(--panel-2); }

</style>
