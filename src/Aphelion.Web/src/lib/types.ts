// Shapes of the data the host sends (camelCase JSON from Aphelion.App/Bridge).

export type LoaderKind = 'vanilla' | 'fabric' | 'quilt' | 'forge' | 'neoForge' | 'legacyForge' | 'optiFine' | 'unknown';
export type MemoryMode = 'auto' | 'fixed';
export type AfterLaunch = 'keep' | 'minimize' | 'close';
export type ProjectType = 'mod' | 'modpack' | 'resourcePack' | 'shader';
export type ContentFolder = 'mods' | 'resourcepacks' | 'shaderpacks';

export interface AppInfo {
  name: string;
  version: string;
  mode: 'standard' | 'portable' | 'override';
  installed: boolean;
  installedVersion: string | null;
  dataRoot: string;
  executable: string;
  os: string;
  runtime: string;
  logFile: string | null;
  uiCulture: string;
  /** First start after a mode switch: where the data came from. */
  movedFrom?: string | null;
  /** First start after an update: the version that was replaced. */
  updatedFrom?: string | null;
}

/** Settings › Download › Connection check: one official host. */
export interface HostCheck { key: string; host: string; ok: boolean; addresses: number; resolveMs: number | null; firstByteMs: number | null; bytesPerSecond: number | null; error: string | null }

export interface UpdateState {
  current: string;
  checking: boolean;
  lastChecked: string | null;
  error: string | null;
  available: { version: string; published: string; size: number; notes: Record<string, string>; page: string | null } | null;
}

export type ModeBlocker = 'same' | 'nested' | 'targetHasData' | 'installed' | 'folderNotEmpty' | 'readOnly' | 'gameRunning' | 'tasksRunning';
export interface ModePlan {
  to: 'standard' | 'portable';
  programFolder: string;
  dataRoot: string;
  copiesProgram: boolean;
  installed: boolean;
  sameVolume: boolean;
  items: { key: 'config' | 'minecraft' | 'runtime'; bytes: number; required: boolean; conflict: boolean }[];
  blocker: ModeBlocker | null;
}

export interface MemorySettings { mode: MemoryMode; fixedMb: number }
export interface ResolutionSettings { width: number; height: number; fullscreen: boolean }

export interface Settings {
  schema: number;
  language: 'auto' | 'zh-Hant' | 'zh-Hans' | 'en';
  theme: 'console' | 'manual' | 'system';
  accent: string;
  motion: 'system' | 'full' | 'reduced';
  download: { concurrency: number };
  checkForUpdates: boolean;
  game: {
    memory: MemorySettings;
    resolution: ResolutionSettings;
    jvmArguments: string;
    gameArguments: string;
    javaPath: string | null;
    afterLaunch: AfterLaunch;
    analyzeCrashes: boolean;
  };
  gameRoots: { path: string; name: string }[];
  activeGameRoot: string | null;
  selectedInstance: string | null;
  extraJavaPaths: string[];
  microsoftClientId: string;
}

export interface GameRootDto { path: string; name: string; exists: boolean; isDefault: boolean; active: boolean }

export interface InstanceOverrides {
  memory?: MemorySettings | null;
  resolution?: ResolutionSettings | null;
  jvmArguments?: string | null;
  gameArguments?: string | null;
  javaPath?: string | null;
  autoJoinServer?: string | null;
}

export interface Instance {
  id: string;
  name: string;
  gameVersion: string | null;
  loader: { kind: LoaderKind; version: string | null };
  isolated: boolean;
  gameDirectory: string;
  directory: string;
  color: string;
  lastPlayed: string | null;
  created: string | null;
  playSeconds: number;
  releaseType: string | null;
  error: string | null;
  overrides: InstanceOverrides;
  modCount: number;
  disabledModCount: number;
}

export interface ManifestVersion { id: string; type: 'release' | 'snapshot' | 'old_beta' | 'old_alpha' | string; releaseTime: string }
export interface LoaderVersion { version: string; stable: boolean; label: string | null }

export interface JavaInstall {
  executable: string;
  home: string;
  major: number;
  version: string;
  arch: string;
  vendor: string | null;
  source: string;
}

export type AccountKind = 'offline' | 'microsoft' | 'yggdrasil';
export interface Account {
  id: string;
  kind: AccountKind;
  name: string;
  uuid: string;
  serverName: string | null;
  server: string | null;
  skinUrl: string | null;
  skinSlim: boolean;
  lastUsed: string | null;
  needsSignIn: boolean;
  added: string;
}
export interface AccountList { selected: string | null; microsoftAvailable: boolean; accounts: Account[] }

export type TaskStatus = 'running' | 'succeeded' | 'failed' | 'cancelled';
export interface Task {
  id: string;
  kind: string;
  title: string;
  status: TaskStatus;
  stage: string | null;
  fraction: number | null;
  detail: string | null;
  bytesDone: number;
  bytesTotal: number;
  bytesPerSecond: number;
  filesDone: number;
  filesTotal: number;
  errorCode: string | null;
  errorMessage: string | null;
  errorArgs: Record<string, string> | null;
  started: string;
  finished: string | null;
  result: unknown;
}

export type LogLevel = 'trace' | 'debug' | 'info' | 'warn' | 'error' | 'fatal';
export interface LogLine { sequence: number; time: string; level: LogLevel; thread: string; logger: string; message: string }

export type SessionState = 'starting' | 'running' | 'exited';
export interface Session {
  id: string;
  instanceId: string;
  instanceName: string;
  processId: number;
  state: SessionState;
  startedAt: string;
  warnings: number;
  errors: number;
  memoryBytes: number;
}

export interface CrashFinding { code: string; priority: number; args: Record<string, string>; evidence: string }
export interface GameExit {
  exitCode: number;
  killedByUser: boolean;
  duration: string;
  crash: { findings: CrashFinding[]; description: string | null; crashReportPath: string | null; jvmCrashLogPath: string | null; exitCode: number } | null;
}

export interface LocalContent {
  fileName: string;
  folder: ContentFolder;
  enabled: boolean;
  size: number;
  sha1: string;
  modified: string;
  id: string | null;
  name: string | null;
  version: string | null;
  description: string | null;
  authors: string | null;
  loader: string | null;
  iconDataUrl: string | null;
  projectId: string | null;
}

export interface ContentUpdate { fileName: string; folder: ContentFolder; projectId: string; currentVersion: string; latest: ModrinthVersion }

export interface World {
  folder: string;
  name: string;
  lastPlayed: string | null;
  gameMode: string | null;
  hardcore: boolean;
  iconDataUrl: string | null;
  path: string;
}

export interface ModrinthHit {
  project_id: string;
  slug: string;
  title: string;
  description: string;
  author: string;
  icon_url: string | null;
  downloads: number;
  follows: number;
  date_modified: string;
  categories: string[];
  display_categories: string[];
  project_type: string;
  license: string | null;
}
export interface ModrinthSearch { hits: ModrinthHit[]; offset: number; limit: number; total_hits: number }

export interface ModrinthProject {
  id: string;
  slug: string;
  title: string;
  description: string;
  body: string;
  icon_url: string | null;
  project_type: string;
  downloads: number;
  categories: string[];
  loaders: string[];
  game_versions: string[];
  license: { id: string; name: string | null } | null;
  source_url: string | null;
  issues_url: string | null;
  wiki_url: string | null;
  gallery: { url: string; title: string | null; featured: boolean }[];
}

export interface ModrinthVersion {
  id: string;
  project_id: string;
  name: string;
  version_number: string;
  version_type: 'release' | 'beta' | 'alpha';
  date_published: string;
  downloads: number;
  loaders: string[];
  game_versions: string[];
  files: { url: string; filename: string; primary: boolean; size: number; hashes: Record<string, string> }[];
  dependencies: { version_id: string | null; project_id: string | null; dependency_type: string }[];
}

export interface StorageInfo {
  config: number;
  runtime: number;
  cache: number;
  logs: number;
  paths: { config: string; runtime: string; cache: string; logs: string; root: string };
}

export interface MemoryStatus { totalMb: number; availableMb: number }
