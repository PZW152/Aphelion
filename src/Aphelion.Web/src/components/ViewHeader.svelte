<script lang="ts">
  import type { Snippet } from 'svelte';
  // Every view starts with this strip; it is also the window's drag handle.
  let { title, subtitle, children }: { title: string; subtitle?: string; children?: Snippet } = $props();
</script>

<header class="drag">
  <div class="heading">
    <h1 class="t-title">{title}</h1>
    {#if subtitle}<span class="sub ellipsis">{subtitle}</span>{/if}
  </div>
  {#if children}<div class="aside">{@render children()}</div>{/if}
</header>

<style>
  header {
    height: var(--header-h); flex: none; display: flex; align-items: center; justify-content: space-between; gap: 24px;
    padding: 0 160px 0 28px; border-bottom: 1px solid var(--rule); min-width: 0;
  }
  .heading { display: flex; align-items: baseline; gap: 14px; min-width: 0; }
  h1 { white-space: nowrap; }
  .sub { font-size: 13px; color: var(--muted); min-width: 0; }
  .aside { display: flex; align-items: center; gap: 8px; flex: none; }
</style>
