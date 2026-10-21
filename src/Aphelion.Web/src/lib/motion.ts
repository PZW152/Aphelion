// Motion system. Every animation in the interface uses these timings and curves, so movement reads as one
// machine: panels slide like hardware, lamps power on with a flicker, nothing bounces for decoration.
// "Reduced" (setting, or the OS preference when the setting follows the system) collapses all of it.
import { cubicOut, quintOut } from 'svelte/easing';
import type { TransitionConfig } from 'svelte/transition';

export const durations = { fast: 140, base: 240, slow: 420 } as const;

/** CSS easing strings matching the JS curves (also declared as --ease-* in app.css). */
export const curves = {
  out: 'cubic-bezier(0.2, 0.7, 0.2, 1)',
  inOut: 'cubic-bezier(0.65, 0, 0.35, 1)',
  spring: 'cubic-bezier(0.34, 1.56, 0.64, 1)',
} as const;

export function reduced(): boolean {
  const mode = document.documentElement.dataset.motion;
  if (mode === 'reduced') return true;
  if (mode === 'full') return false;
  return matchMedia('(prefers-reduced-motion: reduce)').matches;
}

/** Delay for the i-th item of a list entering: a short cascade that never takes longer than ~300 ms. */
export function stagger(i: number, step = 22, cap = 14): number {
  return Math.min(i, cap) * step;
}

/** Rows and panels: rise a few pixels while fading in. */
type Opts = { delay?: number; duration?: number };

export function rise(_node: Element, { delay = 0, y = 8, duration: d = durations.base as number }: Opts & { y?: number } = {}): TransitionConfig {
  if (reduced()) return { duration: 0 };
  return {
    delay,
    duration: d,
    easing: cubicOut,
    css: (t, u) => `opacity:${t};transform:translateY(${u * y}px)`,
  };
}

/** Items slide in from the side they belong to (list rows, toasts). */
export function slide(_node: Element, { delay = 0, x = -12, duration: d = durations.base as number }: Opts & { x?: number } = {}): TransitionConfig {
  if (reduced()) return { duration: 0 };
  return { delay, duration: d, easing: cubicOut, css: (t, u) => `opacity:${t};transform:translateX(${u * x}px)` };
}

/** A panel opening like a hatch: a horizontal slot widening to full size. */
export function hatch(_node: Element, { delay = 0, duration: d = durations.base as number }: Opts = {}): TransitionConfig {
  if (reduced()) return { duration: 0 };
  return {
    delay,
    duration: d,
    easing: quintOut,
    css: (t) => `clip-path: inset(${(1 - t) * 50}% 0 ${(1 - t) * 50}% 0); opacity:${Math.min(1, t * 2)}`,
  };
}

/** Text replaced in place (a value changing): old text leaves upward, new text arrives from below. */
export function ticker(_node: Element, { delay = 0, duration: d = durations.base as number, direction = 1 }: Opts & { direction?: number } = {}): TransitionConfig {
  if (reduced()) return { duration: 0 };
  return { delay, duration: d, easing: cubicOut, css: (t, u) => `opacity:${t};transform:translateY(${u * 0.5 * direction}em)` };
}

/** Lamp powering on: two quick flickers, then steady. */
export function flicker(_node: Element, { delay = 0 }: Opts = {}): TransitionConfig {
  if (reduced()) return { duration: 0 };
  return {
    delay,
    duration: 360,
    css: (t) => {
      const o = t < 0.18 ? t / 0.18 : t < 0.3 ? 0.25 : t < 0.45 ? 1 : t < 0.55 ? 0.5 : 1;
      return `opacity:${o}`;
    },
  };
}

/**
 * Runs a DOM update inside a View Transition when the browser supports it and motion is allowed.
 * `kind` selects the CSS animation set in app.css (page change or theme reveal).
 */
export function viewTransition(update: () => void | Promise<void>, kind: 'page' | 'theme' = 'page', origin?: { x: number; y: number }) {
  const doc = document as Document & { startViewTransition?: (cb: () => void | Promise<void>) => unknown };
  // A hidden document (minimised window) cannot run a transition; the change itself must still happen.
  if (!doc.startViewTransition || reduced() || document.visibilityState !== 'visible') {
    void update();
    return;
  }
  const root = document.documentElement;
  root.dataset.vt = kind;
  if (origin) {
    root.style.setProperty('--vt-x', `${origin.x}px`);
    root.style.setProperty('--vt-y', `${origin.y}px`);
  }
  let applied = false;
  let watchdog = 0;
  const apply = async () => {
    clearTimeout(watchdog);
    if (applied) return;
    applied = true;
    await update();
  };
  try {
    const transition = doc.startViewTransition(apply) as { ready: Promise<void>; finished: Promise<void>; skipTransition(): void };
    // The browser captures the old page on its next frame. If no frame comes (rendering paused, window occluded),
    // the change must not wait for its animation: apply it now and drop the transition.
    watchdog = window.setTimeout(() => {
      if (applied) return;
      applied = true;
      transition.skipTransition();
      void update();
    }, 250);
    // A skipped transition rejects these; the page change is what matters, not the animation.
    transition.ready.catch(() => {});
    transition.finished.catch(() => {}).finally(() => {
      delete root.dataset.vt;
      if (!applied) void update();
    });
  } catch {
    delete root.dataset.vt;
    if (!applied) void update();
  }
}

/**
 * Svelte action: a single indicator that glides to the selected child of a tab bar or segmented control
 * (children marked aria-selected / aria-pressed / aria-current), instead of each item jumping on and off.
 * In a scrolling list the indicator scrolls with the items, since it is positioned inside the list.
 */
export function glide(node: HTMLElement, opts: { axis?: 'x' | 'y' } = {}) {
  const axis = opts.axis ?? 'x';
  const bar = document.createElement('span');
  bar.className = `glide glide-${axis}`;
  bar.setAttribute('aria-hidden', 'true');
  node.style.position ||= 'relative';
  node.appendChild(bar);
  let first = true;

  const place = () => {
    const active = node.querySelector<HTMLElement>('[aria-selected="true"], [aria-pressed="true"], [aria-current="page"], [aria-current="true"]');
    if (!active) {
      bar.style.opacity = '0';
      return;
    }
    // Measure the direct child of the container that holds the active control (a <li> in the rail, the button in a tab bar).
    let box: HTMLElement = active;
    while (box.parentElement && box.parentElement !== node) box = box.parentElement;
    bar.style.transition = first || reduced() ? 'none' : '';
    if (axis === 'x') {
      bar.style.transform = `translateX(${box.offsetLeft}px)`;
      bar.style.width = `${box.offsetWidth}px`;
    } else {
      bar.style.transform = `translateY(${box.offsetTop}px)`;
      bar.style.height = `${box.offsetHeight}px`;
    }
    bar.style.opacity = '1';
    first = false;
  };

  const mutations = new MutationObserver(place);
  mutations.observe(node, { subtree: true, attributes: true, attributeFilter: ['aria-selected', 'aria-pressed', 'aria-current'], childList: true });
  // Items still moving into place (list animations) are measured again once they settle.
  node.addEventListener('transitionend', place);
  node.addEventListener('animationend', place);
  const resize = new ResizeObserver(place);
  resize.observe(node);
  requestAnimationFrame(place);
  return {
    destroy() {
      node.removeEventListener('transitionend', place);
      node.removeEventListener('animationend', place);
      mutations.disconnect();
      resize.disconnect();
      bar.remove();
    },
  };
}
