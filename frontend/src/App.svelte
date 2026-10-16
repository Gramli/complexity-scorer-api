<script lang="ts">
  import { onDestroy, tick } from 'svelte';
  import Icon from './Icon.svelte';

  const promptLimit = 8_000;
  const examples = [
    { title: 'Extract a detail', tag: 'A quick task', prompt: 'Extract the email address from this JSON: {"name":"Alex","email":"alex@example.com"}' },
    { title: 'Compare tradeoffs', tag: 'A few moving parts', prompt: 'Compare PostgreSQL and MongoDB for a small online store. Consider product variants, inventory consistency, reporting, and the experience of a two-person engineering team.' },
    { title: 'Design a system', tag: 'A deeper challenge', prompt: 'Design a multi-region payment system that stays available during a regional outage. Explain how you would prevent duplicate charges, handle delayed events, reconcile inconsistent records, and balance latency, consistency, and operational cost.' },
  ];
  const levels = [
    { value: '0.00', label: 'Trivial', description: 'Extraction, formatting, and direct transformations.' },
    { value: '0.25', label: 'Easy', description: 'A little interpretation and a few straightforward steps.' },
    { value: '0.50', label: 'Moderate', description: 'Several steps, constraints, or pieces of information.' },
    { value: '0.75', label: 'Hard', description: 'Non-obvious tradeoffs and substantial domain knowledge.' },
    { value: '1.00', label: 'Very hard', description: 'Deep analysis across many interacting constraints.' },
  ];

  let prompt = $state('');
  let apiKey = $state('');
  let connectionOpen = $state(true);
  let busy = $state(false);
  let score = $state<number | null>(null);
  let error = $state('');
  let lastPrompt = $state<string | null>(null);
  let lastResponse = $state<unknown>(null);
  let httpStatus = $state<number | null>(null);
  let duration = $state<number | null>(null);
  let retrySeconds = $state(0);
  let promptField: HTMLTextAreaElement;
  let keyField: HTMLInputElement;
  let controller: AbortController | undefined;
  let cooldownTimer: ReturnType<typeof setInterval> | undefined;
  let requestTimer: ReturnType<typeof setTimeout> | undefined;

  const keyAdded = $derived(apiKey.trim().length >= 32 && apiKey.trim().length <= 256);
  const changed = $derived(lastPrompt !== null && lastPrompt !== prompt);
  const nearestLevel = $derived(score === null ? null : levels[Math.round(score * 4)]);
  const requestJson = $derived(lastPrompt === null ? null : JSON.stringify({ prompt: lastPrompt }, null, 2));
  const responseJson = $derived(lastResponse === null ? null : JSON.stringify(lastResponse, null, 2));

  onDestroy(() => {
    controller?.abort();
    clearInterval(cooldownTimer);
    clearTimeout(requestTimer);
  });

  async function loadExample(value: string) {
    prompt = value;
    error = '';
    await tick();
    promptField.focus();
  }

  function startCooldown(header: string | null) {
    if (!header) return;
    const seconds = Number(header);
    const delay = Number.isFinite(seconds) ? seconds : (Date.parse(header) - Date.now()) / 1_000;
    if (!Number.isFinite(delay) || delay <= 0) return;
    const deadline = Date.now() + Math.min(Math.ceil(delay), 3_600) * 1_000;
    retrySeconds = Math.ceil((deadline - Date.now()) / 1_000);
    clearInterval(cooldownTimer);
    cooldownTimer = setInterval(() => {
      retrySeconds = Math.max(0, Math.ceil((deadline - Date.now()) / 1_000));
      if (retrySeconds === 0) clearInterval(cooldownTimer);
    }, 250);
  }

  function friendlyError(status: number): string {
    switch (status) {
      case 400: return 'The API could not accept this prompt. Check the request details below.';
      case 401: return 'The caller API key was not accepted. Check your connection settings.';
      case 413: return 'This request is too large. Try a shorter prompt.';
      case 429: return 'You have reached the request limit. Wait a moment before trying again.';
      case 502: return 'The scoring service could not complete this request. Check the API server configuration.';
      case 503: return 'The scoring service is temporarily busy. Please try again shortly.';
      case 504: return 'The scoring service took too long to respond. Please try again later.';
      default: return `The API returned an error (HTTP ${status}). Please try again later.`;
    }
  }

  async function analyze() {
    if (busy || retrySeconds > 0) return;
    error = '';
    if (!prompt.trim() || prompt.length > promptLimit) {
      error = 'Enter a prompt of up to 8,000 characters.';
      promptField.focus();
      return;
    }
    if (!keyAdded || apiKey.trim().startsWith('tml-')) {
      error = 'Enter your Complexity Scorer caller API key, not your Tinker key.';
      connectionOpen = true;
      await tick();
      keyField.focus();
      return;
    }

    busy = true;
    score = null;
    lastPrompt = prompt;
    lastResponse = null;
    httpStatus = null;
    duration = null;
    connectionOpen = false;
    controller = new AbortController();
    // Longer than the server's maximum configurable request deadline (190 seconds).
    requestTimer = setTimeout(() => controller?.abort(), 200_000);
    const started = performance.now();

    try {
      const response = await fetch('/api/complexity', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Api-Key': apiKey.trim() },
        body: JSON.stringify({ prompt: lastPrompt }),
        signal: controller.signal,
        cache: 'no-store',
        credentials: 'omit',
        redirect: 'error',
      });
      httpStatus = response.status;
      const text = await response.text();
      duration = Math.round(performance.now() - started);
      try {
        lastResponse = JSON.parse(text);
      } catch {
        lastResponse = { message: 'The server returned a non-JSON response.' };
        error = response.ok ? 'The API returned an unexpected response.' : friendlyError(response.status);
        return;
      }
      if (!response.ok) {
        error = friendlyError(response.status);
        if (response.status === 429 || response.status === 503) startCooldown(response.headers.get('Retry-After'));
        if (response.status === 401) {
          connectionOpen = true;
        }
        return;
      }
      const value = (lastResponse as { score?: unknown } | null)?.score;
      if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1) {
        error = 'The API returned an unexpected score. Check the response details below.';
        return;
      }
      score = value;
    } catch (failure) {
      duration = Math.round(performance.now() - started);
      error = failure instanceof DOMException && failure.name === 'AbortError'
        ? 'The request timed out. Please try again later.'
        : 'Could not reach the API. Check that the server is running and your connection is available.';
    } finally {
      clearTimeout(requestTimer);
      controller = undefined;
      busy = false;
      if (httpStatus === 401) {
        connectionOpen = true;
        await tick();
        keyField.focus();
      }
    }
  }

  function handleShortcut(event: KeyboardEvent) {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
      event.preventDefault();
      void analyze();
    }
  }
</script>

<div class="page-shell">
  <header class="site-header">
    <a class="brand" href="/demo/" aria-label="Complexity Scorer home">
      <span class="brand-mark" aria-hidden="true"><span></span><span></span><span></span><span></span></span>
      <span>complexity<span class="brand-light"> / scorer</span></span>
    </a>
    <span class="demo-pill"><span></span>API playground</span>
  </header>

  <main>
    <section class="intro" aria-labelledby="page-title">
      <div class="eyebrow"><span class="short-line"></span>A LITTLE CLARITY BEFORE THE ANSWER</div>
      <h1 id="page-title">Big question.<br />Small question. <em>Find out.</em></h1>
      <p>How much thinking does your prompt need? Put it to the test<br class="desktop-break" /> and turn a question into a complexity signal.</p>
    </section>

    <details class="connection" bind:open={connectionOpen}>
      <summary>
        <span class="connection-title"><Icon name="key" size={18} />Connection settings</span>
        <span class:has-key={keyAdded} class="connection-status">{keyAdded ? 'Caller key added' : 'Add your caller API key'}<Icon name="chevron" size={16} /></span>
      </summary>
      <div class="connection-body">
        <div class="key-control">
          <label for="caller-key">Caller API key</label>
          <input bind:this={keyField} bind:value={apiKey} id="caller-key" type="password" autocomplete="off" spellcheck="false" maxlength="256" placeholder="Paste your Complexity Scorer caller key" aria-describedby="key-help" disabled={busy} />
        </div>
        <p id="key-help">Use the key registered under <code>Api:Clients</code>. It stays in this tab’s memory until you refresh. Your Tinker key belongs on the server.</p>
      </div>
    </details>

    <div class="workspace">
      <section class="composer card" aria-labelledby="prompt-title">
        <div class="card-heading"><span class="step-number">01</span><h2 id="prompt-title">Your prompt</h2><span class="card-aside">THE INPUT</span></div>
        <form onsubmit={(event) => { event.preventDefault(); void analyze(); }}>
          <label class="field-label" for="prompt">What would you ask an AI?</label>
          <div class="editor">
            <textarea bind:this={promptField} bind:value={prompt} id="prompt" name="prompt" rows="7" maxlength={promptLimit} onkeydown={handleShortcut} readonly={busy} placeholder="Type or paste your own prompt here…&#10;&#10;Anything from extracting a single detail to designing a system with a hundred moving parts." aria-describedby="prompt-help character-count"></textarea>
            <div class="editor-footer">
              <span id="prompt-help">Your words. Any question.</span>
              <span id="character-count">{prompt.length.toLocaleString()} <span>/ {promptLimit.toLocaleString()}</span></span>
            </div>
          </div>
          <div class="example-heading">NEED A STARTING POINT?</div>
          <div class="examples">
            {#each examples as example, index}
              <button type="button" class="example" disabled={busy} onclick={() => loadExample(example.prompt)}>
                <span class="example-index">0{index + 1}</span>
                <span><strong>{example.title}</strong><small>{example.tag}</small></span>
                <Icon name="arrow" size={16} />
              </button>
            {/each}
          </div>
          <div class="submit-row">
            <button class="analyze-button" type="submit" disabled={busy || !prompt.trim() || retrySeconds > 0}>
              {#if busy}<span class="spinner" aria-hidden="true"></span>Analyzing…
              {:else if retrySeconds > 0}Try again in {retrySeconds}s
              {:else}Analyze complexity<Icon name="arrow" size={19} />{/if}
            </button>
            <span class="keyboard-hint"><kbd>Ctrl</kbd> + <kbd>↵</kbd></span>
          </div>
        </form>
        {#if error}<div class="error-message" role="alert"><span aria-hidden="true">!</span><p>{error}</p></div>{/if}
      </section>

      <section class="result-card" aria-labelledby="result-title" aria-busy={busy}>
        <div class="card-heading"><span class="step-number">02</span><h2 id="result-title">The signal</h2><Icon name="spark" size={21} /></div>
        <div class="result-content" role="status" aria-live="polite" aria-atomic="true">
          <div class="meter" class:thinking={busy}>
            <svg viewBox="0 0 320 190" fill="none" aria-hidden="true">
              <defs><linearGradient id="meter-gradient" x1="35" y1="160" x2="285" y2="40" gradientUnits="userSpaceOnUse"><stop stop-color="#b9b0fc" /><stop offset="1" stop-color="#e5f0b3" /></linearGradient></defs>
              <path class="meter-track" d="M38 162a122 122 0 0 1 244 0" pathLength="100" />
              <path class="meter-progress" d="M38 162a122 122 0 0 1 244 0" pathLength="100" stroke="url(#meter-gradient)" stroke-dasharray="100" stroke-dashoffset={score === null ? 100 : 100 - score * 100} />
            </svg>
            <div class="meter-reading"><span class="score-value" data-testid="score">{score === null ? '—' : score.toFixed(2)}</span><span class="score-denominator">out of 1.00</span></div>
            <span class="meter-min">0.00</span><span class="meter-max">1.00</span>
          </div>
          <div class="result-description">
            {#if busy}
              <span class="result-label">CONNECTING THE DOTS</span><h3>Thinking about the thinking.</h3><p>The API is evaluating your prompt.</p>
            {:else if nearestLevel}
              <span class="result-label">NEAREST REFERENCE LEVEL</span><h3>{nearestLevel.label}</h3><p>{nearestLevel.description}</p>
            {:else if error}
              <span class="result-label">NO SCORE RETURNED</span><h3>A small interruption.</h3><p>Check the message beside your prompt.</p>
            {:else}
              <span class="result-label">A LITTLE UNKNOWN, FOR NOW</span><h3>Every question starts here.</h3><p>Add your prompt. We’ll measure<br />the reasoning it asks for.</p>
            {/if}
          </div>
          {#if score !== null && duration !== null}
            <div class="result-meta"><span><Icon name="check" size={14} />Scored by the API</span><span>{(duration / 1_000).toFixed(2)}s</span></div>
          {/if}
        </div>
        <div class="result-footnote">{score !== null && changed ? 'Prompt edited. Analyze again to update the score.' : 'Reasoning effort, estimated. Your prompt is evaluated, not answered.'}</div>
      </section>
    </div>

    <section class="reference" aria-labelledby="reference-title">
      <div class="reference-intro"><h2 id="reference-title">A spectrum of thinking.</h2><p>Five reference points. Every shade in between.</p></div>
      <div class="reference-levels">
        {#each levels as level, index}
          <div class="reference-level" class:nearest={score !== null && Math.round(score * 4) === index}>
            <span class="level-value">{level.value}</span><span class="level-line" style={`--level-color: ${['#aaa4a0', '#aaa1be', '#9985d8', '#795bce', '#4e338f'][index]}`}></span><span class="level-label">{level.label}</span>
          </div>
        {/each}
      </div>
    </section>

    <details class="inspector">
      <summary><span><Icon name="code" size={18} />Under the hood<span class="inspector-subtitle">Request & response</span></span><span>{httpStatus === null ? 'POST /api/complexity' : `HTTP ${httpStatus}`}<Icon name="chevron" size={16} /></span></summary>
      <div class="inspector-body">
        <p class="inspector-note">The last submitted prompt and the API’s response. Authentication headers are excluded.</p>
        <div class="payload-grid">
          <div><div class="payload-heading"><span>REQUEST BODY</span><span>application/json</span></div><pre data-testid="request-body">{requestJson ?? '// Your submitted prompt will appear here.'}</pre></div>
          <div><div class="payload-heading"><span>RESPONSE BODY</span><span>{httpStatus === null ? 'Waiting for a request' : `HTTP ${httpStatus}${duration === null ? '' : ` · ${duration} ms`}`}</span></div><pre data-testid="response-body">{responseJson ?? (busy ? '// Waiting for the API…' : '// The actual API response will appear here.')}</pre></div>
        </div>
      </div>
    </details>
    <footer class="site-footer"><span>Complexity Scorer <span class="footer-divider">/</span> A small window into reasoning.</span><span>Scores are estimates, not guarantees.</span></footer>
  </main>
</div>
