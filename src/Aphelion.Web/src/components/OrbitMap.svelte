<script lang="ts">
  // Instance picker drawn as an orbit diagram. All orbits share one focus (the game folder);
  // the selected instance rides the outermost orbit at its far point — the aphelion.
  import type { Instance } from '../lib/types';
  import { t } from '../lib/i18n/index.svelte';

  let { instances, selectedId, onselect }: { instances: Instance[]; selectedId: string | null; onselect: (id: string) => void } = $props();

  const W = 600;
  const H = 500;
  const F = { x: 104, y: 250 };
  const MAX = 6;

  const layout = $derived.by(() => {
    const selected = instances.find((i) => i.id === selectedId) ?? instances[0];
    const others = instances.filter((i) => i !== selected).slice(0, MAX - 1);
    const n = others.length;
    const orbits: { a: number; b: number; cx: number; inst: Instance; x: number; y: number; selected: boolean }[] = [];
    const place = (inst: Instance, a: number, ratio: number, theta: number, isSel: boolean) => {
      const b = a * ratio;
      const c = Math.sqrt(a * a - b * b);
      const cx = F.x + c;
      orbits.push({ a, b, cx, inst, x: cx + a * Math.cos(theta), y: F.y + b * Math.sin(theta), selected: isSel });
    };
    if (selected) place(selected, 240, 0.72, 0, true);
    others.forEach((inst, k) => {
      // Inner orbits are rounder so their bodies stay clear of the focus; bodies alternate between the
      // upper and lower arcs and never sit on the apsis line.
      const a = 196 - (k * 84) / Math.max(1, n - 1 || 1);
      const ratio = 0.76 + (0.14 * k) / Math.max(1, n);
      const step = Math.floor(k / 2) / Math.max(1, Math.ceil(n / 2));
      const theta = k % 2 === 0 ? Math.PI * (0.3 + 0.62 * step) : Math.PI * (1.7 - 0.62 * step);
      place(inst, a, ratio, theta, false);
    });
    return { orbits, hidden: Math.max(0, instances.length - MAX) };
  });

  const pct = (v: number, of: number) => `${(v / of) * 100}%`;
</script>

<div class="map screen-grid">
  <span class="caption t-label">{t('launch.orbit')}</span>
  <span class="count t-mono">{t('launch.orbit.count', { n: instances.length })}</span>

  <div class="frame">
  <svg viewBox="0 0 {W} {H}" aria-hidden="true">
    {#each layout.orbits as o, i (o.inst.id)}
      <ellipse cx={o.cx} cy={F.y} rx={o.a} ry={o.b} fill="none" stroke={o.selected ? 'var(--rule-strong)' : 'var(--rule)'} stroke-dasharray={o.selected ? undefined : '2 5'} />
    {/each}
    <line x1={F.x} y1={F.y} x2={F.x + 470} y2={F.y} stroke="var(--rule)" stroke-dasharray="6 6" />
    <text x={F.x + 230} y={F.y - 8} fill="var(--off)" font-size="11" text-anchor="middle" font-family="var(--font-mono)">r max</text>
    <circle cx={F.x} cy={F.y} r="16" fill="var(--panel)" stroke="var(--ink)" stroke-width="1.5" />
    <circle cx={F.x} cy={F.y} r="5" fill="var(--ink)" />
    <text x={F.x} y={F.y + 38} fill="var(--muted)" font-size="11" text-anchor="middle">{t('launch.orbit.root')}</text>
    {#each layout.orbits.filter((o) => o.selected) as o (o.inst.id)}
      <g class="reticle" style:transform-origin="{o.x}px {o.y}px">
        <circle cx={o.x} cy={o.y} r="22" fill="none" stroke="var(--accent)" />
        <line x1={o.x} y1={o.y - 34} x2={o.x} y2={o.y - 24} stroke="var(--accent)" />
        <line x1={o.x} y1={o.y + 24} x2={o.x} y2={o.y + 34} stroke="var(--accent)" />
        <line x1={o.x - 34} y1={o.y} x2={o.x - 24} y2={o.y} stroke="var(--accent)" />
      </g>
    {/each}
  </svg>

  {#each layout.orbits as o (o.inst.id)}
    <button
      class="sat"
      class:selected={o.selected}
      style:left={pct(o.x, W)}
      style:top={pct(o.y, H)}
      style:--c={o.inst.color}
      aria-pressed={o.selected}
      title={o.inst.name}
      onclick={() => onselect(o.inst.id)}
    >
      <span class="body"></span>
      <span class="name">{o.inst.name}</span>
    </button>
  {/each}
  </div>
  {#if layout.hidden > 0}<span class="more t-label">+{layout.hidden}</span>{/if}
</div>

<style>
  .map { position: relative; overflow: hidden; border: 1px solid var(--rule); min-height: 280px; container-type: size; }
  .caption { position: absolute; left: 16px; top: 12px; z-index: 1; }
  .count { position: absolute; right: 16px; top: 12px; font-size: 12px; color: var(--off); z-index: 1; }
  .more { position: absolute; right: 16px; bottom: 12px; }
  /* A frame with the viewBox's aspect ratio, centred, so SVG coordinates and button percentages agree. */
  .frame { position: absolute; inset: 0; margin: auto; width: min(100cqw, 100cqh * 600 / 500); height: min(100cqh, 100cqw * 500 / 600); }
  svg { position: absolute; inset: 0; width: 100%; height: 100%; }
  .reticle { animation: settle 420ms var(--ease); }
  @keyframes settle { from { transform: scale(1.6) rotate(45deg); opacity: 0; } }

  .sat {
    position: absolute; transform: translate(-50%, -50%); border: 0; background: transparent; padding: 0; cursor: pointer;
    display: flex; flex-direction: column; align-items: center; gap: 4px; color: var(--muted);
  }
  .sat .body { width: 10px; height: 10px; border: 1px solid var(--muted); background: var(--panel); transition: transform 160ms var(--ease); }
  .sat .name { position: absolute; top: 14px; white-space: nowrap; font-size: 12px; max-width: 8.5em; overflow: hidden; text-overflow: ellipsis; }
  .sat:hover .body { transform: scale(1.4); border-color: var(--c); background: var(--c); }
  .sat:hover .name { color: var(--ink); }
  .sat.selected .body { width: 14px; height: 14px; background: var(--accent); border-color: var(--accent); }
  .sat.selected .name { top: auto; bottom: 34px; color: var(--ink); font-weight: 700; font-size: 13px; max-width: 13em; }
  .sat:focus-visible { outline: none; }
  .sat:focus-visible .body { outline: 2px solid var(--accent); outline-offset: 4px; }
</style>
