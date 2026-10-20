<script lang="ts">
  // Instance picker drawn as an orbit diagram. All orbits share one focus (the game folder); the selected
  // instance rides the outermost orbit at its far point — the aphelion. The others sway slowly back and forth
  // along their orbits in unison, like a gimbal settling, so the map stays alive while the spacing between labels
  // (checked in the static layout) never changes — they cannot cross each other or the focus. Selecting another instance moves every body smoothly to
  // its new orbit instead of jumping.
  import { onDestroy } from 'svelte';
  import type { Instance } from '../lib/types';
  import { t } from '../lib/i18n/index.svelte';
  import { reduced } from '../lib/motion';

  let { instances, selectedId, onselect }: { instances: Instance[]; selectedId: string | null; onselect: (id: string) => void } = $props();

  const W = 600;
  const H = 500;
  const F = { x: 104, y: 250 };
  const MAX = 6;
  const OUTER = 240;
  const TAU = Math.PI * 2;
  /** Seconds for one full sway, and how far every body swings either side of its slot. */
  const PERIOD = 40;
  const SWAY = Math.PI * 0.06;

  type Elements = { a: number; ratio: number; theta: number };

  // Where each body should be (excluding drift), recomputed when the instance list or selection changes.
  const targets = $derived.by(() => {
    const selected = instances.find((i) => i.id === selectedId) ?? instances[0];
    const others = instances.filter((i) => i !== selected).slice(0, MAX - 1);
    const n = others.length;
    const map = new Map<string, Elements & { inst: Instance; selected: boolean }>();
    if (selected) map.set(selected.id, { inst: selected, selected: true, a: OUTER, ratio: 0.72, theta: 0 });
    others.forEach((inst, k) => {
      const a = 196 - (k * 84) / Math.max(1, n - 1 || 1);
      const ratio = 0.76 + (0.14 * k) / Math.max(1, n);
      const step = Math.floor(k / 2) / Math.max(1, Math.ceil(n / 2));
      const theta = k % 2 === 0 ? Math.PI * (0.3 + 0.62 * step) : Math.PI * (1.7 - 0.62 * step);
      map.set(inst.id, { inst, selected: false, a, ratio, theta });
    });
    return map;
  });
  const hidden = $derived(Math.max(0, instances.length - MAX));

  // Animated state. `phase` advances each body's sway; `shown` eases toward target + sway.
  let shown = $state<Record<string, Elements>>({});
  let phase = 0;
  let hovered = $state<string | null>(null);

  const shortest = (from: number, to: number) => from + ((((to - from) % TAU) + TAU + Math.PI) % TAU) - Math.PI;

  let frame = 0;
  let last = 0;
  function step(now: number) {
    const dt = last ? Math.min(0.1, (now - last) / 1000) : 0;
    last = now;
    const still = reduced();
    const k = still ? 1 : 1 - Math.exp(-dt / 0.22); // smoothing toward the target
    if (!still) phase = (phase + (TAU / PERIOD) * dt) % TAU;
    const next: Record<string, Elements> = {};
    for (const [id, target] of targets) {
      const sway = target.selected || still ? 0 : SWAY * Math.sin(phase);
      const goal: Elements = { a: target.a, ratio: target.ratio, theta: target.theta + sway };
      const cur = shown[id];
      if (!cur) {
        // New bodies enter from the focus outward.
        next[id] = still ? goal : { a: goal.a * 0.35, ratio: goal.ratio, theta: goal.theta };
        continue;
      }
      next[id] = {
        a: cur.a + (goal.a - cur.a) * k,
        ratio: cur.ratio + (goal.ratio - cur.ratio) * k,
        theta: cur.theta + (shortest(cur.theta, goal.theta) - cur.theta) * k,
      };
    }
    shown = next;
    frame = requestAnimationFrame(step);
  }
  frame = requestAnimationFrame(step);
  onDestroy(() => cancelAnimationFrame(frame));

  const bodies = $derived(
    [...targets.values()].map((target) => {
      const e = shown[target.inst.id] ?? { a: target.a, ratio: target.ratio, theta: target.theta };
      const b = e.a * e.ratio;
      const c = Math.sqrt(Math.max(0, e.a * e.a - b * b));
      const cx = F.x + c;
      return { ...target, rx: e.a, ry: b, cx, x: cx + e.a * Math.cos(e.theta), y: F.y + b * Math.sin(e.theta) };
    }),
  );
  const selectedBody = $derived(bodies.find((b) => b.selected));
  const pct = (v: number, of: number) => `${(v / of) * 100}%`;
</script>

<div class="map screen-grid ticks">
  <span class="caption t-label">{t('launch.orbit')}</span>
  <span class="count t-mono">{t('launch.orbit.count', { n: instances.length })}</span>

  <div class="frame">
    <svg viewBox="0 0 {W} {H}" aria-hidden="true">
      {#each bodies as o, i (o.inst.id)}
        <ellipse
          class="orbit"
          class:outer={o.selected}
          class:lit={hovered === o.inst.id}
          style:--i={i}
          pathLength="1"
          cx={o.cx}
          cy={F.y}
          rx={o.rx}
          ry={o.ry}
        />
      {/each}
      <line class="apsis" x1={F.x} y1={F.y} x2={F.x + 470} y2={F.y} />
      <text x={F.x + 230} y={F.y - 8} class="legend" text-anchor="middle">r max</text>
      <circle cx={F.x} cy={F.y} r="16" fill="var(--panel)" stroke="var(--ink)" stroke-width="1.5" />
      <circle cx={F.x} cy={F.y} r="5" fill="var(--ink)" />
      <text x={F.x} y={F.y + 38} fill="var(--muted)" font-size="11" text-anchor="middle">{t('launch.orbit.root')}</text>
      {#if selectedBody}
        {#key selectedBody.inst.id}
          <!-- Position on the outer group; the inner group animates around its own origin. -->
          <g transform="translate({selectedBody.x} {selectedBody.y})"><g class="reticle">
            <circle class="ping" r="22" />
            <circle r="22" fill="none" stroke="var(--accent)" />
            <g class="ticks-rot">
              <line x1="0" y1="-34" x2="0" y2="-26" />
              <line x1="0" y1="26" x2="0" y2="34" />
              <line x1="-34" y1="0" x2="-26" y2="0" />
              <line x1="26" y1="0" x2="34" y2="0" />
            </g>
          </g></g>
        {/key}
      {/if}
    </svg>

    {#each bodies as o (o.inst.id)}
      <button
        class="sat"
        class:selected={o.selected}
        style:left={pct(o.x, W)}
        style:top={pct(o.y, H)}
        style:--c={o.inst.color}
        aria-pressed={o.selected}
        title={o.inst.name}
        onclick={() => onselect(o.inst.id)}
        onpointerenter={() => (hovered = o.inst.id)}
        onpointerleave={() => (hovered = null)}
        onfocus={() => (hovered = o.inst.id)}
        onblur={() => (hovered = null)}
      >
        <span class="body"></span>
        <span class="name">{o.inst.name}</span>
      </button>
    {/each}
  </div>
  {#if hidden > 0}<span class="more t-label">+{hidden}</span>{/if}
</div>

<style>
  .map { position: relative; overflow: hidden; border: 1px solid var(--rule); min-height: 280px; container-type: size; }
  .caption { position: absolute; left: 16px; top: 12px; z-index: 2; }
  .count { position: absolute; right: 16px; top: 12px; font-size: 12px; color: var(--off); z-index: 2; }
  .more { position: absolute; right: 16px; bottom: 12px; z-index: 2; }
  /* A frame with the viewBox's aspect ratio, centred, so SVG coordinates and button percentages agree. */
  .frame { position: absolute; inset: 0; margin: auto; width: min(100cqw, 100cqh * 600 / 500); height: min(100cqh, 100cqw * 500 / 600); }
  svg { position: absolute; inset: 0; width: 100%; height: 100%; overflow: visible; }

  .orbit { fill: none; stroke: var(--rule); stroke-dasharray: 0.004 0.01; transition: stroke var(--t-base); }
  .orbit.outer { stroke: var(--rule-strong); stroke-dasharray: none; }
  .orbit.lit { stroke: var(--muted); stroke-dasharray: none; }
  .apsis { stroke: var(--rule); stroke-dasharray: 6 6; }
  .legend { fill: var(--off); font-size: 11px; font-family: var(--font-mono); }

  /* First appearance: orbits trace themselves, the apsis line extends from the focus. */
  .orbit { animation: orbit-in 900ms var(--ease) both; animation-delay: calc(var(--i, 0) * 90ms); }
  .orbit.outer { stroke-dasharray: 1; animation: trace 1100ms var(--ease-in-out) both; }
  .apsis { animation: extend 700ms var(--ease) both 200ms; transform-origin: 104px 250px; }
  @keyframes trace { from { stroke-dashoffset: 1; } to { stroke-dashoffset: 0; } }
  @keyframes orbit-in { from { opacity: 0; } }
  @keyframes extend { from { transform: scaleX(0); } }

  .reticle line { stroke: var(--accent); }
  .reticle { animation: settle 520ms var(--ease) both; }
  .ticks-rot { animation: spin 24s linear infinite; }
  .ping { fill: none; stroke: var(--accent); transform-box: fill-box; transform-origin: center; animation: ping 3.2s var(--ease) infinite 600ms; opacity: 0; }
  @keyframes settle { from { opacity: 0; transform: scale(1.8) rotate(45deg); } }
  @keyframes spin { to { transform: rotate(360deg); } }
  @keyframes ping { 0% { opacity: 0.7; transform: scale(1); } 70%, 100% { opacity: 0; transform: scale(2.4); } }

  .sat {
    position: absolute; transform: translate(-50%, -50%); border: 0; background: transparent; padding: 0; cursor: pointer;
    display: flex; flex-direction: column; align-items: center; gap: 4px; color: var(--muted); z-index: 1;
    animation: sat-in 500ms var(--ease) both 300ms;
  }
  @keyframes sat-in { from { opacity: 0; } }
  .sat .body { width: 10px; height: 10px; border: 1px solid var(--muted); background: var(--panel); transition: transform var(--t-base) var(--ease-spring), background var(--t-fast), border-color var(--t-fast), width var(--t-base), height var(--t-base); }
  .sat .name { position: absolute; top: 14px; white-space: nowrap; font-size: 12px; max-width: 8.5em; overflow: hidden; text-overflow: ellipsis; transition: color var(--t-fast); }
  .sat:hover .body, .sat:focus-visible .body { transform: scale(1.5) rotate(45deg); border-color: var(--c); background: var(--c); }
  .sat:hover .name { color: var(--ink); }
  .sat.selected .body { width: 14px; height: 14px; background: var(--accent); border-color: var(--accent); transform: none; }
  .sat.selected .name { top: auto; bottom: 34px; color: var(--ink); font-weight: 700; font-size: 13px; max-width: 13em; }
  .sat:focus-visible { outline: none; }
  .sat:focus-visible .body { outline: 2px solid var(--accent); outline-offset: 4px; }
</style>
