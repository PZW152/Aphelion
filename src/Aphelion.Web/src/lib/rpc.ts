// Bridge to the host (Aphelion.App/Bridge/RpcRouter.cs). Outside WebView2 a mock backend is used,
// so the interface can be developed and reviewed in an ordinary browser.

export class RpcError extends Error {
  constructor(public code: string, message: string, public args: Record<string, string> = {}) {
    super(message);
  }
}

type Listener = (data: any) => void;

interface WebView {
  postMessage(message: string): void;
  postMessageWithAdditionalObjects(message: string, objects: ArrayLike<unknown>): void;
  addEventListener(type: 'message', handler: (e: MessageEvent) => void): void;
}

const webview: WebView | undefined = (window as any).chrome?.webview;
export const inHost = !!webview;

let nextId = 1;
const pending = new Map<number, { resolve: (v: any) => void; reject: (e: RpcError) => void }>();
const listeners = new Map<string, Set<Listener>>();

function dispatch(event: string, data: unknown) {
  listeners.get(event)?.forEach((l) => {
    try {
      l(data);
    } catch (e) {
      console.error(`listener for ${event} failed`, e);
    }
  });
}

if (webview) {
  webview.addEventListener('message', (e: MessageEvent) => {
    const msg = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
    if (msg && typeof msg.event === 'string') {
      dispatch(msg.event, msg.data);
      return;
    }
    const entry = pending.get(msg?.id);
    if (!entry) return;
    pending.delete(msg.id);
    if (msg.error) entry.reject(new RpcError(msg.error.code, msg.error.message, msg.error.args ?? {}));
    else entry.resolve(msg.result);
  });
}

let mock: ((method: string, params: any, emit: (e: string, d: unknown) => void) => Promise<any>) | null = null;

export async function call<T = unknown>(method: string, params?: unknown): Promise<T> {
  if (webview) {
    const id = nextId++;
    return new Promise<T>((resolve, reject) => {
      pending.set(id, { resolve, reject });
      webview.postMessage(JSON.stringify({ id, method, params: params ?? {} }));
    });
  }
  // The mock is only part of development builds; production bundles never contain it.
  if (!import.meta.env.DEV) throw new RpcError('rpc.noHost', 'Aphelion must run inside its desktop window.');
  if (!mock) mock = (await import('./mock')).handle;
  try {
    return (await mock(method, params ?? {}, dispatch)) as T;
  } catch (e: any) {
    throw e instanceof RpcError ? e : new RpcError('unexpected', String(e?.message ?? e));
  }
}

export function on(event: string, listener: Listener): () => void {
  let set = listeners.get(event);
  if (!set) listeners.set(event, (set = new Set()));
  set.add(listener);
  return () => set!.delete(listener);
}

/** Sends dropped files to the host, which receives their real paths. */
export function sendFiles(context: string, files: FileList | File[]) {
  if (webview && files.length > 0) webview.postMessageWithAdditionalObjects(context, files as ArrayLike<unknown>);
}
