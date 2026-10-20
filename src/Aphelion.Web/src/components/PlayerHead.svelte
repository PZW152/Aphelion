<script lang="ts">
  // The 8×8 face (plus hat layer) cropped from a skin texture; a built-in face when there is no skin.
  let { skinUrl = null, uuid = '', size = 44 }: { skinUrl?: string | null; uuid?: string; size?: number } = $props();
  let canvas: HTMLCanvasElement;

  // Fallback faces: Steve-like and Alex-like palettes, chosen from the UUID like the game does.
  const faces = [
    { hair: '#3b2a1e', skin: '#b98a68', eye: '#3f4fa0', mouth: '#6b4430' },
    { hair: '#c2701b', skin: '#e3b28f', eye: '#2e6d3a', mouth: '#a5604a' },
  ];

  function drawFallback(ctx: CanvasRenderingContext2D) {
    const f = faces[uuid ? parseInt(uuid.at(-1) ?? '0', 16) % 2 : 0];
    ctx.fillStyle = f.skin; ctx.fillRect(0, 0, 8, 8);
    ctx.fillStyle = f.hair; ctx.fillRect(0, 0, 8, 2); ctx.fillRect(0, 2, 1, 1); ctx.fillRect(7, 2, 1, 1);
    ctx.fillStyle = '#f2f2f2'; ctx.fillRect(1, 4, 2, 1); ctx.fillRect(5, 4, 2, 1);
    ctx.fillStyle = f.eye; ctx.fillRect(2, 4, 1, 1); ctx.fillRect(5, 4, 1, 1);
    ctx.fillStyle = f.mouth; ctx.fillRect(3, 6, 2, 1);
  }

  $effect(() => {
    const ctx = canvas.getContext('2d')!;
    ctx.imageSmoothingEnabled = false;
    ctx.clearRect(0, 0, 8, 8);
    drawFallback(ctx);
    if (!skinUrl) return;
    const img = new Image();
    img.referrerPolicy = 'no-referrer';
    img.onload = () => {
      const scale = img.width / 64;
      ctx.clearRect(0, 0, 8, 8);
      ctx.drawImage(img, 8 * scale, 8 * scale, 8 * scale, 8 * scale, 0, 0, 8, 8);
      ctx.drawImage(img, 40 * scale, 8 * scale, 8 * scale, 8 * scale, 0, 0, 8, 8);
    };
    img.src = skinUrl;
  });
</script>

<canvas bind:this={canvas} width="8" height="8" style:width="{size}px" style:height="{size}px" aria-hidden="true"></canvas>

<style>
  canvas { image-rendering: pixelated; flex: none; display: block; }
</style>
