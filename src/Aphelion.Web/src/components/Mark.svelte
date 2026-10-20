<script lang="ts">
  // The Aphelion mark: an orbit with its focus body, and the craft at the far point.
  let { size = 34 }: { size?: number } = $props();
</script>

<svg width={size} height={size} viewBox="0 0 34 34" aria-hidden="true">
  <ellipse cx="17" cy="17" rx="13" ry="8" fill="none" stroke="var(--ink)" stroke-width="1.5" />
  <circle cx="7.4" cy="17" r="2.6" fill="var(--ink)" />
  <circle cx="30" cy="17" r="3" fill="var(--accent)" />
</svg>
