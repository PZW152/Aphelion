// Development-only stand-in for the host, used when the page runs in a normal browser (npm run dev).
// It mirrors the real method names with believable sample data so the interface can be reviewed.
import type { Instance, Settings, Task, LocalContent, ModrinthHit } from './types';

type Emit = (event: string, data: unknown) => void;

const now = Date.now();
const iso = (msAgo: number) => new Date(now - msAgo).toISOString();

const settings: Settings = {
  schema: 1, language: 'auto', theme: 'console', accent: '#FF5B14', motion: 'system',
  download: { concurrency: 24 },
  checkForUpdates: true,
  game: { memory: { mode: 'auto', fixedMb: 4096 }, resolution: { width: 1280, height: 720, fullscreen: false }, jvmArguments: '', gameArguments: '', javaPath: null, afterLaunch: 'keep', analyzeCrashes: true },
  gameRoots: [{ path: 'C:\\Users\\Pilot\\AppData\\Local\\Aphelion\\minecraft', name: 'Aphelion' }, { path: 'D:\\Games\\.minecraft', name: 'Games' }],
  activeGameRoot: 'D:\\Games\\.minecraft', selectedInstance: '主線生存', extraJavaPaths: [], microsoftClientId: '',
};

const base = (id: string, name: string, gv: string, kind: Instance['loader']['kind'], lv: string | null, color: string, last: number | null, mods: number, play: number): Instance => ({
  id, name, gameVersion: gv, loader: { kind, version: lv }, isolated: kind !== 'vanilla', gameDirectory: `D:\\Games\\.minecraft\\versions\\${id}`,
  directory: `D:\\Games\\.minecraft\\versions\\${id}`, color, lastPlayed: last === null ? null : iso(last), created: iso(86400000 * 40),
  playSeconds: play, releaseType: 'release', error: null, overrides: {}, modCount: mods, disabledModCount: mods ? 2 : 0,
});

const instances: Instance[] = [
  base('主線生存', '主線生存', '1.21.1', 'fabric', '0.16.9', '#FF5B14', 7_200_000, 48, 226_800),
  base('機械動力', '機械動力', '1.20.1', 'forge', '47.3.0', '#4FA3FF', 86_400_000 * 3, 212, 90_000),
  base('光影測試', '光影測試', '1.21.1', 'fabric', '0.16.9', '#F2B33D', 86_400_000 * 8, 6, 5_400),
  base('1.21.4', '原版 1.21.4', '1.21.4', 'vanilla', null, '#E6ECE8', 86_400_000 * 18, 0, 12_000),
  base('空島生存', '空島生存', '1.20.4', 'neoForge', '20.4.237', '#6FCF97', null, 96, 0),
];

const accounts = {
  selected: 'a1', microsoftAvailable: false,
  accounts: [
    { id: 'a1', kind: 'microsoft', name: 'Kestrel_07', uuid: '0123456789abcdef0123456789abcdef', serverName: null, server: null, skinUrl: null, skinSlim: false, lastUsed: iso(7_200_000), needsSignIn: false, added: iso(86400000 * 90) },
    { id: 'a2', kind: 'offline', name: 'Apollo_11', uuid: 'fedcba9876543210fedcba9876543210', serverName: null, server: null, skinUrl: null, skinSlim: false, lastUsed: null, needsSignIn: false, added: iso(86400000 * 30) },
    { id: 'a3', kind: 'yggdrasil', name: 'Orbiter', uuid: '11112222333344445555666677778888', serverName: 'LittleSkin', server: 'https://littleskin.cn/api/yggdrasil/', skinUrl: null, skinSlim: true, lastUsed: iso(86400000 * 6), needsSignIn: true, added: iso(86400000 * 60) },
  ],
};

const tasks: Task[] = [];
const sessions: any[] = [];

const mods: [string, string, string, boolean, string][] = [
  ['Fabric API', 'Core hooks and compatibility layer for Fabric mods.', '0.107.0', true, 'fabric-api'],
  ['Sodium', 'A modern rendering engine that greatly improves frame rates.', '0.6.0', true, 'sodium'],
  ['Lithium', 'Optimises game physics, mob AI and block ticking.', '0.13.1', true, 'lithium'],
  ['Iris Shaders', 'Shader pack support compatible with OptiFine packs.', '1.8.1', true, 'iris'],
  ['Xaero’s Minimap', 'Live minimap with waypoints.', '24.6.1', true, 'xaerominimap'],
  ['Just Enough Items', 'Item and recipe browser.', '19.21.0', true, 'jei'],
  ['Distant Horizons', 'Level-of-detail rendering for very long view distances.', '2.3.0', false, 'distanthorizons'],
  ['Mod Menu', 'A menu listing installed mods and their settings.', '11.0.3', true, 'modmenu'],
];

const hits: ModrinthHit[] = [
  ['Xaero’s Minimap', 'xaero96', 'Displays a live minimap, waypoints and nearby entities.', 48_120_000],
  ['JourneyMap', 'techbrew', 'Real-time mapping in game or in a web browser as you explore.', 22_060_000],
  ['VoxelMap Updated', 'MamiyaOtaru', 'A lightweight minimap with waypoints and a cave view.', 3_880_000],
  ['Xaero’s World Map', 'xaero96', 'A full-screen world map that shares waypoints with the minimap.', 39_770_000],
  ['Antique Atlas 4', 'Sisby', 'A hand-drawn atlas item that records what you explore.', 410_000],
].map(([title, author, description, downloads], i) => ({
  project_id: `p${i}`, slug: String(title).toLowerCase().replace(/\W+/g, '-'), title: String(title), description: String(description), author: String(author),
  icon_url: null, downloads: Number(downloads), follows: 1000, date_modified: iso(86400000 * (i + 1) * 3), categories: ['fabric', 'utility'],
  display_categories: ['utility'], project_type: 'mod', license: 'LGPL-3.0',
}));

function startTask(emit: Emit, kind: string, title: string, stages: string[], result: unknown, onDone?: () => void): string {
  const id = Math.random().toString(16).slice(2, 14);
  const task: Task = {
    id, kind, title, status: 'running', stage: stages[0], fraction: 0, detail: null, bytesDone: 0, bytesTotal: 0, bytesPerSecond: 0,
    filesDone: 0, filesTotal: 0, errorCode: null, errorMessage: null, errorArgs: null, started: new Date().toISOString(), finished: null, result: null,
  };
  tasks.unshift(task);
  emit('tasks.changed', [{ ...task }]);
  let step = 0;
  const timer = setInterval(() => {
    step++;
    const stageIndex = Math.min(stages.length - 1, Math.floor(step / 6));
    task.stage = stages[stageIndex];
    task.fraction = (step % 6) / 6;
    task.filesTotal = 6288;
    task.filesDone = Math.min(6288, Math.round(6288 * task.fraction));
    task.bytesTotal = 412_000_000;
    task.bytesDone = Math.round(task.bytesTotal * task.fraction);
    task.bytesPerSecond = 18_400_000;
    if (step >= stages.length * 6) {
      clearInterval(timer);
      Object.assign(task, { status: 'succeeded', fraction: 1, finished: new Date().toISOString(), result });
      onDone?.();
    }
    emit('tasks.changed', [{ ...task }]);
  }, 350);
  return id;
}

const sampleLog = [
  ['info', 'main', 'Loading Minecraft 1.21.1 with Fabric Loader 0.16.9'],
  ['info', 'main', 'Loading 48 mods:\n\t- fabric-api 0.107.0+1.21.1\n\t- sodium 0.6.0+mc1.21.1\n\t- lithium 0.13.1+mc1.21.1'],
  ['warn', 'main', "Mod 'xaerominimap' uses a deprecated mixin config"],
  ['info', 'Datafixer Bootstrap', '226 Datafixer optimizations took 214 milliseconds'],
  ['info', 'Render thread', 'Setting user: Kestrel_07'],
  ['info', 'Render thread', 'Backend library: LWJGL version 3.3.3+5'],
  ['info', 'Render thread', 'OpenGL 4.6 · NVIDIA GeForce RTX 4070'],
  ['info', 'Render thread', 'Reloading ResourceManager: vanilla, fabric, sodium, iris'],
  ['info', 'Render thread', 'Sound engine started'],
];

export async function handle(method: string, p: any, emit: Emit): Promise<unknown> {
  await new Promise((r) => setTimeout(r, 60));
  switch (method) {
    case 'app.info':
      return { name: 'Aphelion', version: '0.1.0', mode: 'standard', installed: false, installedVersion: null, dataRoot: 'C:\\Users\\Pilot\\AppData\\Local\\Aphelion', executable: 'C:\\Users\\Pilot\\Downloads\\Aphelion.exe', os: 'Microsoft Windows NT 10.0.26100.0', runtime: '10.0.13', logFile: 'C:\\Users\\Pilot\\AppData\\Local\\Aphelion\\logs\\aphelion-20261020.log', uiCulture: navigator.language };
    case 'settings.get':
      return structuredClone(settings);
    case 'settings.update': {
      const merge = (t: any, s: any) => { for (const k of Object.keys(s)) { if (s[k] && typeof s[k] === 'object' && !Array.isArray(s[k]) && t[k]) merge(t[k], s[k]); else t[k] = s[k]; } };
      merge(settings, p);
      return structuredClone(settings);
    }
    case 'roots.list':
      return settings.gameRoots.map((r) => ({ ...r, exists: true, isDefault: r.name === 'Aphelion', active: r.path === settings.activeGameRoot }));
    case 'instances.list':
      return structuredClone(instances);
    case 'instances.select':
      settings.selectedInstance = p.id;
      return null;
    case 'accounts.list':
      return structuredClone(accounts);
    case 'accounts.select':
      accounts.selected = p.id;
      emit('accounts.changed', null);
      return null;
    case 'tasks.list':
      return tasks;
    case 'game.sessions':
      return structuredClone(sessions);
    case 'system.memory':
      return { totalMb: 32_768, availableMb: 19_870 };
    case 'window.state':
      return { maximized: false };
    case 'java.list':
      return [
        { executable: 'C:\\Users\\Pilot\\AppData\\Local\\Aphelion\\runtime\\java-runtime-delta\\bin\\javaw.exe', home: '…\\runtime\\java-runtime-delta', major: 21, version: '21.0.5', arch: 'x64', vendor: 'Microsoft', source: 'managed' },
        { executable: 'C:\\Program Files\\Eclipse Adoptium\\jdk-17.0.9.9-hotspot\\bin\\javaw.exe', home: 'C:\\Program Files\\Eclipse Adoptium\\jdk-17.0.9.9-hotspot', major: 17, version: '17.0.9', arch: 'x64', vendor: 'Eclipse Adoptium', source: 'registry' },
        { executable: 'C:\\Program Files\\Java\\jre1.8.0_421\\bin\\javaw.exe', home: 'C:\\Program Files\\Java\\jre1.8.0_421', major: 8, version: '1.8.0_421', arch: 'x64', vendor: 'Oracle Corporation', source: 'registry' },
      ];
    case 'versions.list': {
      const releases = ['1.21.4', '1.21.3', '1.21.2', '1.21.1', '1.21', '1.20.6', '1.20.4', '1.20.2', '1.20.1', '1.19.4', '1.18.2', '1.16.5', '1.12.2', '1.8.9', '1.7.10'];
      const versions = [
        { id: '24w45a', type: 'snapshot', releaseTime: iso(86400000 * 20) },
        ...releases.map((id, i) => ({ id, type: 'release', releaseTime: iso(86400000 * 60 * (i + 1)) })),
        { id: 'b1.7.3', type: 'old_beta', releaseTime: iso(86400000 * 5000) },
      ];
      return { latest: { release: '1.21.4', snapshot: '24w45a' }, versions };
    }
    case 'loaders.list':
      if (p.kind === 'fabric' || p.kind === 'quilt') return [{ version: '0.16.9', stable: true, label: null }, { version: '0.16.8', stable: true, label: null }, { version: '0.16.10-beta.1', stable: false, label: null }];
      if (p.kind === 'forge') return [{ version: '47.3.12', stable: true, label: 'latest' }, { version: '47.3.0', stable: true, label: 'recommended' }];
      return [{ version: '21.1.77', stable: true, label: null }];
    case 'instances.create':
      return startTask(emit, 'install', p.id, ['install.metadata', 'install.client', 'install.loader', 'install.files', 'install.finalize'], p.id, () => {
        instances.unshift(base(p.id, p.displayName || p.id, p.gameVersion, p.loader, p.loaderVersion, p.color ?? '#5FD4D0', null, 0, 0));
        emit('instances.changed', null);
      });
    case 'instances.repair':
      return startTask(emit, 'repair', p.id, ['repair.scan', 'repair.download'], 3);
    case 'launch.start': {
      const inst = instances.find((i) => i.id === p.instanceId)!;
      const session = { id: 's' + Date.now(), instanceId: inst.id, instanceName: inst.name, processId: 18244, state: 'starting', startedAt: new Date().toISOString(), warnings: 1, errors: 0, memoryBytes: 2_810_000_000 };
      return startTask(emit, 'launch', inst.name, ['launch.verify', 'launch.java', 'launch.auth', 'launch.natives', 'launch.arguments', 'launch.ignition'], session.id, () => {
        sessions.unshift(session);
        emit('game.started', session);
        sampleLog.forEach(([level, thread, message], i) => setTimeout(() => {
          emit('game.log', [{ sessionId: session.id, line: { sequence: i + 1, time: new Date().toISOString(), level, thread, logger: '', message } }]);
          if (i === 4) { session.state = 'running'; emit('game.state', { ...session }); }
        }, 300 * i));
      });
    }
    case 'content.list':
      if (p.folder !== 'mods') return [];
      return mods.map(([name, description, version, enabled, id], i): LocalContent => ({
        fileName: `${id}-${version}.jar${enabled ? '' : '.disabled'}`, folder: 'mods', enabled, size: 400_000 + i * 120_000, sha1: String(i).repeat(40).slice(0, 40), modified: iso(86400000 * i),
        id, name, version, description, authors: null, loader: 'fabric', iconDataUrl: null, projectId: null,
      }));
    case 'instances.saves':
      return [
        { folder: 'New World', name: '新的世界', lastPlayed: iso(7_200_000), gameMode: 'survival', hardcore: false, iconDataUrl: null, path: '' },
        { folder: 'Creative Lab', name: '建築實驗室', lastPlayed: iso(86400000 * 12), gameMode: 'creative', hardcore: false, iconDataUrl: null, path: '' },
      ];
    case 'modrinth.search':
      return { hits, offset: 0, limit: 24, total_hits: hits.length };
    case 'modrinth.project':
      return { id: p.id, slug: 'xaeros-minimap', title: 'Xaero’s Minimap', description: hits[0].description, body: '## Features\n\n- Live minimap with **cave mode**\n- Waypoints shared with the world map\n- Entity radar\n\nWorks client-side only.', icon_url: null, project_type: 'mod', downloads: 48_120_000, categories: ['utility'], loaders: ['fabric', 'forge', 'neoforge'], game_versions: ['1.21.1'], license: { id: 'LGPL-3.0', name: null }, source_url: null, issues_url: 'https://example.org', wiki_url: null, gallery: [] };
    case 'modrinth.versions':
      return [{ id: 'v1', project_id: p.id, name: '24.6.1', version_number: '24.6.1', version_type: 'release', date_published: iso(86400000 * 6), downloads: 100000, loaders: ['fabric'], game_versions: ['1.21.1'], files: [], dependencies: [{ version_id: null, project_id: 'P7dR8mSH', dependency_type: 'required' }] }];
    case 'app.storage':
      return { config: 12_000, runtime: 412_000_000, cache: 96_000_000, logs: 2_400_000, paths: { config: '…\\Aphelion\\config', runtime: '…\\Aphelion\\runtime', cache: '…\\Aphelion\\cache', logs: '…\\Aphelion\\logs', root: '…\\Aphelion' } };
    case 'app.modePlan': {
      const folder: string | null = p.folder ?? null;
      const programFolder = folder ? folder + '\\Aphelion' : 'C:\\Users\\Pilot\\Downloads';
      return {
        to: p.to, programFolder, dataRoot: programFolder + '\\Data', copiesProgram: !!folder, installed: false,
        sameVolume: !folder || folder.startsWith('C:'),
        items: [
          { key: 'config', bytes: 14_200, required: true, conflict: false },
          { key: 'minecraft', bytes: 6_480_000_000, required: false, conflict: false },
          { key: 'runtime', bytes: 412_000_000, required: false, conflict: false },
        ],
        blocker: null,
      };
    }
    case 'app.switchMode': {
      const steps = ['config', 'minecraft', 'runtime'].filter((k) => k === 'config' || p.items.includes(k));
      const total = 6_892_214_200;
      for (let i = 0; i <= 40; i++) {
        await new Promise((r) => setTimeout(r, 80));
        emit('mode.progress', { item: steps[Math.min(steps.length - 1, Math.floor((i / 40) * steps.length))], done: (total * i) / 40, total });
      }
      return { programFolder: '', dataRoot: '' };
    }
    case 'dialog.pickFolder':
      return 'E:\\';
    case 'update.state':
    case 'update.check': {
      const state = {
        current: '0.2.0', checking: false, lastChecked: new Date().toISOString(), error: null,
        available: { version: '0.3.0', published: iso(3_600_000), size: 2_640_000, page: 'https://github.com/PZW152/Aphelion/releases/tag/v0.3.0',
          notes: { 'zh-Hant': '- 連線檢查顯示每個官方伺服器的速度\n- 大檔案分段下載，斷線續傳\n- 修正：視窗縮小時的版面', en: '- Connection check\n- Ranged downloads with resume' } },
      };
      if (method === 'update.check') emit('update.changed', state);
      return state;
    }
    case 'update.install': {
      for (let i = 0; i <= 30; i++) {
        await new Promise((r) => setTimeout(r, 60));
        emit('update.progress', { done: (2_640_000 * i) / 30, total: 2_640_000, rate: 1_200_000 });
      }
      return { version: '0.3.0' };
    }
    case 'network.check':
      await new Promise((r) => setTimeout(r, 1400));
      return [
        { key: 'mojang-meta', host: 'piston-meta.mojang.com', ok: true, addresses: 4, resolveMs: 18, firstByteMs: 142, bytesPerSecond: null, error: null },
        { key: 'mojang-data', host: 'piston-data.mojang.com', ok: true, addresses: 4, resolveMs: 21, firstByteMs: 188, bytesPerSecond: 4_820_000, error: null },
        { key: 'mojang-libraries', host: 'libraries.minecraft.net', ok: true, addresses: 4, resolveMs: 16, firstByteMs: 160, bytesPerSecond: null, error: null },
        { key: 'mojang-assets', host: 'resources.download.minecraft.net', ok: true, addresses: 6, resolveMs: 14, firstByteMs: 121, bytesPerSecond: null, error: null },
        { key: 'fabric', host: 'meta.fabricmc.net', ok: true, addresses: 2, resolveMs: 25, firstByteMs: 310, bytesPerSecond: null, error: null },
        { key: 'forge', host: 'maven.minecraftforge.net', ok: true, addresses: 2, resolveMs: 30, firstByteMs: 820, bytesPerSecond: 640_000, error: null },
        { key: 'neoforge', host: 'maven.neoforged.net', ok: false, addresses: 1, resolveMs: 40, firstByteMs: null, bytesPerSecond: null, error: 'timeout' },
        { key: 'quilt', host: 'meta.quiltmc.org', ok: true, addresses: 2, resolveMs: 22, firstByteMs: 205, bytesPerSecond: null, error: null },
        { key: 'modrinth', host: 'api.modrinth.com', ok: true, addresses: 2, resolveMs: 19, firstByteMs: 260, bytesPerSecond: null, error: null },
      ];
    default:
      return null;
  }
}
