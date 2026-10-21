// Global application state (Svelte 5 runes) and the actions that keep it in sync with the host.
import { tick } from 'svelte';
import { call, on } from './rpc';
import { viewTransition } from './motion';
import { describeError, errorText, resolveLang, setLang, t } from './i18n/index.svelte';
import type {
  AccountList, AppInfo, GameExit, GameRootDto, HostCheck, Instance, JavaInstall, LogLine, MemoryStatus, Session, Settings, Task, UpdateState,
} from './types';

export type Route = 'launch' | 'instances' | 'download' | 'accounts' | 'telemetry' | 'settings';
export const routes: Route[] = ['launch', 'instances', 'download', 'accounts', 'telemetry', 'settings'];

export interface Toast { id: number; kind: 'info' | 'success' | 'error'; text: string; ms: number }
export interface ExitRecord { session: Session; exit: GameExit; at: number }

export const app = $state({
  ready: false,
  route: 'launch' as Route,
  info: null as AppInfo | null,
  settings: null as Settings | null,
  instances: [] as Instance[],
  instancesLoaded: false,
  roots: [] as GameRootDto[],
  accounts: { selected: null, microsoftAvailable: false, accounts: [] } as AccountList,
  tasks: [] as Task[],
  sessions: [] as Session[],
  logs: {} as Record<string, LogLine[]>,
  exits: [] as ExitRecord[],
  java: [] as JavaInstall[],
  memory: null as MemoryStatus | null,
  update: null as UpdateState | null,
  /** Last connection check; drives the network annunciator. */
  network: null as HostCheck[] | null,
  toasts: [] as Toast[],
  maximized: false,
  // Cross-view navigation hints.
  focusInstance: null as string | null,
  instanceTab: 'overview' as string,
  downloadTab: 'versions' as string,
  telemetrySession: null as string | null,
  createOpen: false,
  createVersion: null as string | null,
  launching: {} as Record<string, string>,
  accountDialog: null as null | 'offline' | 'microsoft' | 'yggdrasil' | 'menu',
});

export const MAX_LOG_LINES = 8000;

let toastId = 0;
export function toast(text: string, kind: Toast['kind'] = 'info', ms = 4200) {
  const id = ++toastId;
  const life = kind === 'error' ? ms * 2 : ms;
  app.toasts.push({ id, kind, text, ms: life });
  toastTimers.set(id, { timer: setTimeout(() => dismissToast(id), life), deadline: Date.now() + life, remaining: life });
}

// Dismissal timers pause while the pointer rests on a message, so it can be read to the end.
const toastTimers = new Map<number, { timer: ReturnType<typeof setTimeout> | null; deadline: number; remaining: number }>();
export function pauseToast(id: number) {
  const t = toastTimers.get(id);
  if (!t || !t.timer) return;
  clearTimeout(t.timer);
  t.timer = null;
  t.remaining = Math.max(0, t.deadline - Date.now());
}
export function resumeToast(id: number) {
  const t = toastTimers.get(id);
  if (!t || t.timer) return;
  t.deadline = Date.now() + t.remaining;
  t.timer = setTimeout(() => dismissToast(id), t.remaining);
}
export function dismissToast(id: number) {
  const timer = toastTimers.get(id)?.timer;
  if (timer) clearTimeout(timer);
  toastTimers.delete(id);
  const i = app.toasts.findIndex((t) => t.id === id);
  if (i >= 0) app.toasts.splice(i, 1);
}
export function fail(e: unknown) {
  toast(describeError(e), 'error');
}

export function go(route: Route) {
  if (app.route === route) return;
  // Pages are stacked in rail order: going down the rail moves the console up, and the reverse.
  document.documentElement.dataset.vtDir = routes.indexOf(route) > routes.indexOf(app.route) ? 'down' : 'up';
  viewTransition(async () => {
    app.route = route;
    await tick();
  });
}

export const selectedInstance = () => {
  const id = app.settings?.selectedInstance;
  return app.instances.find((i) => i.id === id) ?? app.instances[0] ?? null;
};
export const selectedAccount = () => app.accounts.accounts.find((a) => a.id === app.accounts.selected) ?? null;
export const runningTasks = () => app.tasks.filter((t) => t.status === 'running');
export const liveSessions = () => app.sessions.filter((s) => s.state !== 'exited');

export function applyTheme(s: Settings) {
  const root = document.documentElement;
  const dark = s.theme === 'system' ? matchMedia('(prefers-color-scheme: dark)').matches : s.theme === 'console';
  root.dataset.theme = dark ? 'console' : 'manual';
  root.style.setProperty('--accent', s.accent);
  root.dataset.motion = s.motion ?? 'system';
  setLang(resolveLang(s.language, app.info?.uiCulture ?? navigator.language));
}

export async function refreshSettings() {
  app.settings = await call<Settings>('settings.get');
  applyTheme(app.settings);
}

export async function updateSettings(patch: Record<string, unknown>) {
  try {
    app.settings = await call<Settings>('settings.update', patch);
    applyTheme(app.settings);
  } catch (e) {
    fail(e);
  }
}

export async function refreshInstances() {
  try {
    const [instances, roots] = await Promise.all([call<Instance[]>('instances.list'), call<GameRootDto[]>('roots.list')]);
    app.instances = instances;
    app.roots = roots;
  } catch (e) {
    fail(e);
  } finally {
    app.instancesLoaded = true;
  }
}

export async function refreshAccounts() {
  app.accounts = await call<AccountList>('accounts.list');
}

export async function refreshJava(refresh = false) {
  app.java = await call<JavaInstall[]>('java.list', { refresh });
}

export async function selectInstance(id: string) {
  if (app.settings) app.settings.selectedInstance = id;
  await call('instances.select', { id }).catch(fail);
}

// ---- Task tracking ----

const taskWatchers = new Map<string, (t: Task) => void>();

/** Runs the callback once the task finishes (success, failure or cancel). */
export function watchTask(id: string, done: (t: Task) => void) {
  const existing = app.tasks.find((t) => t.id === id);
  if (existing && existing.status !== 'running') done(existing);
  else taskWatchers.set(id, done);
}

/** Standard toast reporting for tasks the user started. */
export function reportTask(id: string, success: (t: Task) => string | null) {
  watchTask(id, (task) => {
    if (task.status === 'succeeded') {
      const text = success(task);
      if (text) toast(text, 'success');
    } else if (task.status === 'failed') {
      toast(errorText(task.errorCode, task.errorMessage, task.errorArgs), 'error');
    }
  });
}

function upsertTasks(batch: Task[]) {
  for (const task of batch) {
    const i = app.tasks.findIndex((t) => t.id === task.id);
    if (i >= 0) app.tasks[i] = task;
    else app.tasks.unshift(task);
    if (task.status !== 'running') {
      const w = taskWatchers.get(task.id);
      if (w) {
        taskWatchers.delete(task.id);
        w(task);
      }
    }
  }
}

/** The running launch task of an instance, if any. */
export const launchTask = (instanceId: string) => {
  const id = app.launching[instanceId];
  return id ? app.tasks.find((t) => t.id === id && t.status === 'running') ?? null : null;
};

export async function launch(instance: Instance) {
  if (!selectedAccount()) {
    app.accountDialog = 'menu';
    go('accounts');
    toast(t('error.account.none'), 'error');
    return;
  }
  try {
    const id = await call<string>('launch.start', { instanceId: instance.id });
    app.launching[instance.id] = id;
    watchTask(id, (task) => {
      delete app.launching[instance.id];
      if (task.status === 'failed') toast(errorText(task.errorCode, task.errorMessage, task.errorArgs), 'error');
      if (task.status === 'succeeded') app.telemetrySession = String(task.result);
    });
  } catch (e) {
    fail(e);
  }
}

// ---- Startup and host events ----

export async function start() {
  app.info = await call<AppInfo>('app.info');
  await refreshSettings();
  await Promise.all([refreshInstances(), refreshAccounts(), refreshJava().catch(() => {})]);
  app.tasks = await call<Task[]>('tasks.list');
  app.sessions = await call<Session[]>('game.sessions');
  app.memory = await call<MemoryStatus>('system.memory');
  app.maximized = (await call<{ maximized: boolean }>('window.state')).maximized;
  app.ready = true;
  if (app.info.movedFrom) toast(t('mode.moved', { path: app.info.dataRoot }), 'success');
  if (app.info.updatedFrom) toast(t('update.updated', { v: app.info.version }), 'success');
  app.update = await call<UpdateState>('update.state').catch(() => null);

  setInterval(async () => {
    app.memory = await call<MemoryStatus>('system.memory').catch(() => app.memory);
    if (liveSessions().length > 0) (await call<Session[]>('game.sessions').catch(() => [] as Session[])).forEach(upsertSession);
  }, 3000);
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => app.settings && applyTheme(app.settings));
}

on('settings.changed', (s: Settings) => {
  const rootChanged = app.settings?.activeGameRoot !== s.activeGameRoot;
  app.settings = s;
  applyTheme(s);
  if (rootChanged) refreshInstances();
});
on('accounts.changed', () => refreshAccounts().catch(fail));
on('instances.changed', () => refreshInstances());
on('tasks.changed', (batch: Task[]) => upsertTasks(batch));
on('window.state', (s: { maximized: boolean }) => (app.maximized = s.maximized));
// A newly found release is announced once per version.
let announced: string | null = null;
on('update.changed', (s: UpdateState) => {
  app.update = s;
  const v = s.available?.version;
  if (v && v !== announced) {
    announced = v;
    toast(t('update.toast', { v }));
  }
});

const stateRank = { starting: 0, running: 1, exited: 2 } as const;
function upsertSession(s: Session) {
  const i = app.sessions.findIndex((x) => x.id === s.id);
  // Events can arrive out of order; a session never moves back to an earlier state.
  if (i >= 0) { if (stateRank[s.state] >= stateRank[app.sessions[i].state]) app.sessions[i] = s; }
  else app.sessions.unshift(s);
}
on('game.started', (s: Session) => {
  upsertSession(s);
  app.logs[s.id] = [];
  app.telemetrySession = s.id;
});
on('game.state', (s: Session) => upsertSession(s));
on('game.log', (batch: { sessionId: string; line: LogLine }[]) => {
  for (const { sessionId, line } of batch) {
    const lines = (app.logs[sessionId] ??= []);
    lines.push(line);
    if (lines.length > MAX_LOG_LINES) lines.splice(0, lines.length - MAX_LOG_LINES);
  }
});
on('game.exited', (e: { session: Session; exit: GameExit }) => {
  upsertSession(e.session);
  app.exits.unshift({ ...e, at: Date.now() });
  if (e.exit.crash) {
    app.telemetrySession = e.session.id;
    go('telemetry');
  }
});
