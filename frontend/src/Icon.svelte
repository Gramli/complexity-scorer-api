<script lang="ts">
  let { name, size = 20 }: { name: 'arrow' | 'spark' | 'code' | 'key' | 'chevron' | 'check' | 'close'; size?: number } = $props();
</script>

<svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
  {#if name === 'arrow'}
    <path d="M5 12h14m-6-6 6 6-6 6" />
  {:else if name === 'spark'}
    <path d="m12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5L12 3Z" />
  {:else if name === 'code'}
    <path d="m8 7-5 5 5 5m8-10 5 5-5 5m-3-14-2 18" />
  {:else if name === 'key'}
    <circle cx="8" cy="9" r="4" /><path d="m11 12 9 9m-5-5 3-3m-1 5 3-3" />
  {:else if name === 'chevron'}
    <path d="m6 9 6 6 6-6" />
  {:else if name === 'check'}
    <path d="m5 12 4 4L19 6" />
  {:else}
    <path d="m6 6 12 12M6 18 18 6" />
  {/if}
</svg>
