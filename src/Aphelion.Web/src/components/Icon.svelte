<script lang="ts">
  // Stroke icons drawn on a 16px grid, matching the 1.5px rules of the console.
  const paths: Record<string, string> = {
    folder: 'M2 4h4l1.5 1.5H14V13H2z',
    gear: 'M8 5.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5zM8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.4 3.4l1.4 1.4M11.2 11.2l1.4 1.4M3.4 12.6l1.4-1.4M11.2 4.8l1.4-1.4',
    plus: 'M8 2v12M2 8h12',
    search: 'M6.5 2a4.5 4.5 0 1 0 0 9 4.5 4.5 0 0 0 0-9zM10 10l4 4',
    close: 'M3 3l10 10M13 3L3 13',
    minimize: 'M3 8.5h10',
    maximize: 'M3.5 3.5h9v9h-9z',
    restore: 'M5.5 3.5h7v7M3.5 5.5h7v7h-7z',
    trash: 'M3 4.5h10M6.5 4.5V3h3v1.5M4.5 4.5l.7 9h5.6l.7-9',
    download: 'M8 2v8.5M4.5 7L8 10.5 11.5 7M3 13.5h10',
    refresh: 'M13 8a5 5 0 1 1-1.5-3.6M13 2.5v3h-3',
    check: 'M3 8.5l3 3 7-7',
    alert: 'M8 2l6.5 11.5h-13zM8 6.5v3M8 11.5v.5',
    play: 'M5 3l8 5-8 5z',
    stop: 'M4 4h8v8H4z',
    external: 'M9 3h4v4M13 3L7.5 8.5M11.5 9.5V13H3V4.5h3.5',
    copy: 'M5.5 5.5h7v7h-7zM3.5 10.5v-7h7',
    chevronRight: 'M6 3l5 5-5 5',
    chevronDown: 'M3 6l5 5 5-5',
    user: 'M8 2.5a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5zM3 13.5c.5-2.5 2.5-4 5-4s4.5 1.5 5 4',
    key: 'M10.5 2.5a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM8.4 7.6L3 13M5 11l1.5 1.5M3.5 12.5L5 14',
    globe: 'M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2zM2 8h12M8 2c1.8 1.7 2.6 3.7 2.6 6S9.8 12.3 8 14C6.2 12.3 5.4 10.3 5.4 8S6.2 3.7 8 2z',
    wrench: 'M10.5 2a3.5 3.5 0 0 0-3.2 4.8L2.5 11.6l1.9 1.9 4.8-4.8A3.5 3.5 0 0 0 14 5.5l-2 2-2-.5-.5-2 2-2z',
    cube: 'M8 1.5l5.5 3v7L8 14.5l-5.5-3v-7zM2.5 4.5L8 7.5l5.5-3M8 7.5v7',
    box: 'M2.5 5h11v8.5h-11zM2 2.5h12V5H2zM6.5 7.5h3',
    image: 'M2.5 3h11v10h-11zM2.5 11l3.5-3.5 3 3 2-2 2.5 2.5M10.5 5.5h.5',
    sun: 'M8 5a3 3 0 1 0 0 6 3 3 0 0 0 0-6zM8 1v1.5M8 13.5V15M1 8h1.5M13.5 8H15M3 3l1 1M12 12l1 1M3 13l1-1M12 4l1-1',
    globeGrid: 'M2 3h12v10H2zM2 6.5h12M2 9.5h12M6 3v10M10 3v10',
    terminal: 'M2 3h12v10H2zM4.5 6.5L6.5 8l-2 1.5M8 10h3.5',
    layers: 'M8 2l6 3-6 3-6-3zM2 8l6 3 6-3M2 11l6 3 6-3',
    sliders: 'M3 4h6M12 4h1M3 8h1M7 8h6M3 12h7M13 12h0M10.5 2.5v3M5.5 6.5v3M11.5 10.5v3',
    archive: 'M2 3h12v3H2zM3 6v7.5h10V6M6.5 9h3',
    link: 'M6.5 9.5l3-3M7 4.5l1-1a2.5 2.5 0 0 1 3.5 3.5l-1 1M9 11.5l-1 1A2.5 2.5 0 0 1 4.5 9l1-1',
    signal: 'M2.5 13.5v-2M6 13.5v-5M9.5 13.5v-8M13 13.5V2.5',
    clock: 'M8 2a6 6 0 1 0 0 12A6 6 0 0 0 8 2zM8 4.5V8l2.5 1.5',
  };
  let { name, size = 16, stroke = 1.5, label }: { name: string; size?: number; stroke?: number; label?: string } = $props();
</script>

<svg
  width={size}
  height={size}
  viewBox="0 0 16 16"
  fill="none"
  stroke="currentColor"
  stroke-width={stroke}
  stroke-linecap="square"
  stroke-linejoin="miter"
  role={label ? 'img' : undefined}
  aria-label={label}
  aria-hidden={label ? undefined : 'true'}><path d={paths[name] ?? ''} /></svg>
