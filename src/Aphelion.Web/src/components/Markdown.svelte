<script lang="ts">
  // Project descriptions come from third parties: rendered from Markdown, then sanitised.
  import { marked } from 'marked';
  import DOMPurify from 'dompurify';

  let { source }: { source: string } = $props();

  const html = $derived(
    DOMPurify.sanitize(marked.parse(source ?? '', { async: false, gfm: true, breaks: false }) as string, {
      FORBID_TAGS: ['style', 'form', 'input', 'button', 'iframe', 'object', 'embed', 'video', 'audio'],
      FORBID_ATTR: ['style', 'onerror', 'onload'],
      ALLOWED_URI_REGEXP: /^(?:https?:|#)/i,
    }),
  );
</script>

<div class="md selectable">{@html html}</div>

<style>
  .md { font-size: 14px; line-height: 1.65; color: var(--ink-3); overflow-wrap: anywhere; }
  .md :global(h1), .md :global(h2), .md :global(h3) { font-size: 15px; font-weight: 700; color: var(--ink); margin: 18px 0 8px; }
  .md :global(p) { margin: 0 0 10px; }
  .md :global(ul), .md :global(ol) { padding-left: 20px; margin: 0 0 10px; list-style: disc; }
  .md :global(img) { max-width: 100%; height: auto; margin: 8px 0; }
  .md :global(code) { font-family: var(--font-mono); font-size: 12px; background: var(--screen); padding: 1px 4px; }
  .md :global(pre) { background: var(--screen); padding: 10px; overflow: auto; }
  .md :global(table) { border-collapse: collapse; }
  .md :global(td), .md :global(th) { border: 1px solid var(--rule); padding: 4px 8px; }
  .md :global(hr) { border: 0; border-top: 1px solid var(--rule); }
</style>
