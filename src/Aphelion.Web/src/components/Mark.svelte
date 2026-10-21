<script lang="ts">
  // The Aphelion mark: an orbit with its focus body, and the craft at the far point.
  // While a task or game is active the craft travels its orbit — the logo doubles as an activity light.
  import { reduced } from '../lib/motion';

  let { size = 34, active = false }: { size?: number; active?: boolean } = $props();
  const moving = $derived(active && !reduced());
</script>

<svg width={size} height={size} viewBox="0 0 34 34" aria-hidden="true">
  <ellipse cx="17" cy="17" rx="13" ry="8" fill="none" stroke="var(--ink)" stroke-width="1.5" />
  <circle cx="7.4" cy="17" r="2.6" fill="var(--ink)" />
  {#if moving}
    <circle r="3" fill="var(--accent)">
      <animateMotion dur="3.2s" repeatCount="indefinite" path="M30,17 A13,8 0 1,1 4,17 A13,8 0 1,1 30,17" />
    </circle>
  {:else}
    <circle cx="30" cy="17" r="3" fill="var(--accent)" />
  {/if}
</svg>

<style>
  svg { flex: none; overflow: visible; }
</style>
