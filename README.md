# Complexity Scorer API

Complexity Scorer estimates how much reasoning a prompt needs. Your application can use that estimate to choose which AI model should answer it, such as a cheaper model for a simple task or a more capable model for a difficult one.

Send a prompt to `POST /api/complexity` and receive a score between `0.00` and `1.00`. This endpoint evaluates the prompt; it does not answer it.

| Score | Level | Example of the work involved |
| --- | --- | --- |
| `0.00` | Trivial | Extract a value or format text |
| `0.25` | Easy | Interpret a short request or follow a few simple steps |
| `0.50` | Moderate | Combine information and follow several constraints |
| `0.75` | Hard | Analyze tradeoffs or use substantial domain knowledge |
| `1.00` | Very hard | Perform deep analysis across complex systems |

Scores between these reference points are allowed. They are estimates, not guarantees that a particular model can solve the task.

## Before you start

You need:

- The **.NET 10 SDK**, which builds and runs this project. Check installed SDKs with `dotnet --list-sdks` and look for a `10.0` version.
- **PowerShell** for the commands below.
- A **Tinker API key** with access to the configured model checkpoint.
- An internet connection so the API can contact Tinker.

Open PowerShell in the folder containing `complexity-scorer-api.csproj`. Run the commands below from that folder.

## Understand the two API keys

An API key is a secret used to identify an authorized caller. This project uses two different keys:

| Key | Purpose | Where you configure it |
| --- | --- | --- |
| Tinker API key | Allows this server to call Tinker | `Tinker:ApiKey` |
| Caller API key | Allows your application to call Complexity Scorer | `Api:Clients:<client-id>` |

The request flow is:

```text
Your application -- caller API key --> Complexity Scorer -- Tinker API key --> Tinker
```

Your application sends its caller key in the `X-Api-Key` header. The Tinker key stays on the Complexity Scorer server.

## Run locally: your first request

### 1. Save your Tinker key

In your first PowerShell window, replace `YOUR_TINKER_API_KEY` with your actual Tinker key:

```powershell
dotnet user-secrets set "Tinker:ApiKey" "YOUR_TINKER_API_KEY" --project complexity-scorer-api.csproj
```

**User secrets** store development settings outside the project folder, so you do not need to put credentials in `appsettings.json`. They are for local development, not production secret storage.

### 2. Generate and save a caller key

Run this in the same window. It generates a random secret and registers a client named `local`:

```powershell
$keyBytes = New-Object byte[] 32
$keyGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$keyGenerator.GetBytes($keyBytes)
$keyGenerator.Dispose()
$callerApiKey = [Convert]::ToBase64String($keyBytes)

dotnet user-secrets set "Api:Clients:local" "$callerApiKey" --project complexity-scorer-api.csproj
```

**Keep this window open.** Step 4 uses the `$callerApiKey` variable from this window.

The API requires a Tinker key and at least one valid caller key before it will start.

### 3. Start the API in a second window

Open a second PowerShell window in the same project folder. Trust the local HTTPS certificate, then start the server:

```powershell
dotnet dev-certs https --trust
dotnet run --project complexity-scorer-api.csproj --launch-profile https
```

Accept the certificate trust prompt if it appears. The certificate lets your computer trust this local HTTPS server.

Wait for the terminal to show that the application is listening at `https://localhost:7092`. Leave it running. You can stop it later with `Ctrl+C`.

### 4. Send a prompt from the first window

Return to the first PowerShell window, where `$callerApiKey` is still available:

```powershell
$headers = @{ 'X-Api-Key' = $callerApiKey }
$body = @{
    prompt = 'Extract the email address from this JSON: {"email":"hello@example.com"}'
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri 'https://localhost:7092/api/complexity' -Headers $headers -ContentType 'application/json' -Body $body
```

PowerShell displays the returned score. The underlying JSON response looks like this; your actual score may differ:

```json
{ "score": 0.08 }
```

The score is rounded to two decimal places. JSON tools may display `0.50` as `0.5`; both mean the same thing.

If you closed the first window, repeat step 2 to generate a new key, restart the API, and use the new key for your request.

## Try the Svelte playground

The small demo at `https://localhost:7092/demo/` lets you type or paste **your own prompt**, load and edit an example, and see the actual score on an animated meter. An expandable inspector shows the last JSON request and response, with authentication headers excluded.

Install **Node.js 24 LTS** as well as the .NET SDK. After configuring the API keys above, build the frontend from the repository root:

```powershell
npm --prefix frontend ci
npm --prefix frontend run build
dotnet run --project complexity-scorer-api.csproj --launch-profile https
```

Open `https://localhost:7092/demo/`, expand **Connection settings**, and paste a dedicated **caller API key** registered under `Api:Clients`. Type a prompt and select **Analyze complexity**, or press `Ctrl+Enter` (`Cmd+Enter` on macOS). Example buttons only fill the editor; you can edit or replace their text before submitting.

The caller key stays in page memory and is cleared on refresh; it is not bundled or saved in browser storage. A browser user can inspect their own outgoing key, so use a dedicated demo key. The Tinker key stays on the server. Prompts and responses are kept in this tab until refresh, and there are no automatic scoring retries.

The page calls `/api/complexity` on the same origin, retaining API-key authentication and API rate limits. It accepts up to the default 8,000-character prompt limit; the API remains authoritative if you configure stricter limits. The display labels the nearest of the five rubric reference points, while preserving the actual numerical score. A score estimates reasoning effort; it is not the answer to the submitted prompt.

Frontend source lives in `frontend/`. Vite writes compiled HTML/CSS/JavaScript into `wwwroot/demo/`; rebuild after frontend edits and before `dotnet publish`. Generated assets and `node_modules` are ignored by Git. Node is only needed to develop or build the frontend, not to serve the compiled page.

For frontend development with live reload, keep the API running and use a second terminal:

```powershell
npm --prefix frontend run dev
```

Open the printed `/demo/` URL. Vite proxies `/api` to the local HTTPS API; its development proxy accepts the local development certificate. Production requests go directly to the same-origin API.

Frontend validation and browser tests:

```powershell
npm --prefix frontend run check
npm --prefix frontend run build
npm --prefix frontend test
```

Browser tests use installed Microsoft Edge in headless mode and simulate API responses, so they do not call Tinker. They verify arbitrary user prompts, editing examples, keyboard submission, API-key handling, error states, retry delays, safe text rendering, and mobile layout. To use another installed browser or Playwright Chromium, adjust `frontend/playwright.config.ts` accordingly. The .NET tests also verify that demo hosting preserves HTTPS and API authentication.

## Call the endpoint from another tool

For Postman or another HTTP client, use:

| Request setting | Value |
| --- | --- |
| Method | `POST` |
| URL | `https://localhost:7092/api/complexity` |
| `Content-Type` header | `application/json` |
| `X-Api-Key` header | Your configured caller key |
| Body | A JSON object containing a `prompt` string |

Example body:

```json
{ "prompt": "Explain the tradeoffs between two database designs." }
```

Use the same caller key you registered in step 2. You can view it locally with `$callerApiKey` in the first PowerShell window; treat that output as a secret.

Only send the `prompt` field. It must contain text and cannot be empty or just spaces. Extra fields, such as `model`, are rejected.

The [HTTP request file](complexity-scorer-api.http) contains another example. During local development, `/openapi/v1.json` provides the API description for tools; it also requires `X-Api-Key`. There is no Swagger UI configured.

## What does `Api:Clients` contain?

`Clients` lists the applications allowed to call this API. Each entry maps a **client ID** to a **secret caller key**:

```json
{
  "Api": {
    "Clients": {
      "my-app": "REPLACE_WITH_A_RANDOM_SECRET_FOR_MY_APP",
      "another-app": "REPLACE_WITH_A_DIFFERENT_RANDOM_SECRET"
    }
  }
}
```

This example shows the structure only. Store real keys in user secrets or your hosting platform's secret store rather than committing them to a configuration file.

- `my-app` is a label for the client. You can use a GUID instead of a name.
- The value is the secret the client sends in `X-Api-Key`. The client does not send the label.
- Each client gets its own request allowance. Two applications sharing a key also share that allowance.
- Keys must be unique and contain 32 to 256 printable ASCII characters without spaces. Use the random-key generator above for new keys.

Restart the API after adding or changing client keys.

## Base URL and checkpoint: what is the difference?

These settings are already configured in [appsettings.json](appsettings.json):

```json
{
  "Tinker": {
    "BaseUrl": "https://tinker.thinkingmachines.dev/services/tinker-prod/oai/api/v1/",
    "Model": "tinker://c2148695-43ab-5758-aaff-bd78ddda980d:train:0/sampler_weights/final"
  }
}
```

`BaseUrl` is the HTTPS address of Tinker's service. Keep the trailing `/`: the client adds `chat/completions` to build the request address.

`Model` selects the saved model weights, called a **checkpoint**, that Tinker should use. The `tinker://...` value goes in the request's `model` field. It is not a URL you open in a browser or put into `BaseUrl`.

The checkpoint must still exist, and your Tinker account must have access to it. Automated tests do not check either condition.

## Settings you may want to change

Edit non-secret settings in [appsettings.json](appsettings.json), then restart the API. A colon in a setting name means a nested JSON property: `Api:WindowSeconds` means `WindowSeconds` inside `Api`.

**Rate limiting** restricts how often clients can call the API. **Concurrency limiting** restricts how many requests can be handled at the same time.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Api:RequestsPerWindow` | `30` | Each client can make up to 30 scoring requests per time window |
| `Api:GlobalRequestsPerWindow` | `120` | Shared limit for requests reaching the limiter, including requests with invalid keys |
| `Api:WindowSeconds` | `60` | Each counting window lasts 60 seconds |
| `Api:MaxConcurrentRequests` | `4` | At most four requests are handled at once; extra requests are rejected without waiting |
| `Api:MaxPromptCharacters` | `8000` | Maximum prompt length; some symbols, such as emoji, count as two characters |
| `Api:MaxRequestBytes` | `65536` | Maximum size of the complete request body: 64 KiB |
| `Tinker:TimeoutSeconds` | `30` | Maximum time spent waiting for Tinker, including reading its response |
| `Tinker:MaxOutputTokens` | `50` | Maximum model output length, measured in tokens (pieces of text) |
| `Tinker:Temperature` | `0` | Requests minimal sampling randomness |
| `Tinker:ReasoningEffort` | `""` (omitted) | Optional thinking-effort setting for models that support it; accepted strings are `none`, `minimal`, `low`, `medium`, `high`, and `xhigh` |

The whole request has a separate deadline of `Tinker:TimeoutSeconds + 10` seconds, or 40 seconds by default. Requests are not automatically retried because another Tinker call may incur another charge.

### Use environment variables instead of user secrets

Environment variables are another way to supply configuration, particularly when hosting the API. Replace `:` with `__` in the setting name:

| Configuration setting | Environment variable |
| --- | --- |
| `Tinker:ApiKey` | `Tinker__ApiKey` |
| `Api:Clients:local` | `Api__Clients__local` |
| `Api:RequestsPerWindow` | `Api__RequestsPerWindow` |

`TINKER_API_KEY` is also accepted as a fallback when `Tinker:ApiKey` is empty. Configure secrets through your hosting platform rather than putting them in source control.

## Troubleshooting

When a request fails, the API usually returns a JSON error object with a `status` and a short `title`. This format is called **Problem Details**. If Tinker returned an HTTP error, the response also includes `upstreamStatusCode`, which is Tinker's status code. For example, your API may return `502` with `upstreamStatusCode: 401`, meaning Tinker rejected the server's Tinker key. Detailed Tinker responses are not returned to callers.

| Symptom or status | What to check |
| --- | --- |
| `dotnet` is not recognized | Install the .NET 10 SDK and open a new terminal |
| The API fails at startup with a configuration error | Check the Tinker key and caller key, including the caller key's length and uniqueness |
| HTTPS certificate error | Run `dotnet dev-certs https --trust`, accept the prompt, and retry |
| Connection refused | Run with `--launch-profile https` and wait for `Now listening on: https://localhost:7092`. If only `http://localhost:5066` appears, stop the API and restart with the HTTPS profile |
| `400` | Use HTTPS and send valid JSON with only a non-empty `prompt` string within the length limit |
| `401` | Send the caller key in `X-Api-Key`; do not send the client label or the Tinker key |
| `404` | Use `/api/complexity`; visiting `/` in a browser does not call this endpoint |
| `405` | Use `POST`, not `GET`; typing the endpoint into a browser's address bar sends `GET` |
| `413` | Reduce the size of the JSON request body |
| `415` | Set `Content-Type` to `application/json` |
| `429` | Wait for the number of seconds in the `Retry-After` response header before retrying |
| `502` | Check server logs, Tinker access, the checkpoint, and the model compatibility notes below |
| `503` | Tinker is busy or limiting requests; wait according to `Retry-After` |
| `504` | The request timed out; check Tinker availability and your configured timeout |
| `500` | An unexpected server error occurred; inspect the server logs |

For a `502`, check `upstreamStatusCode` or the server log line `Tinker returned HTTP ...`:

| Tinker status | What to check |
| --- | --- |
| `401` | Verify `Tinker:ApiKey` is a valid Tinker key, then restart the API |
| `403` | Check that the Tinker account can access the configured model |
| `404` | Check the Tinker base URL and whether the checkpoint still exists |
| `400` or `422` | Check whether the model supports the supplied settings, especially `reasoning_effort`; see the compatibility notes below |
| `500` or another server error | Check Tinker availability |

If you need help debugging, share the status codes and sanitized error title. Keep API keys private.

`reasoning_effort: "false"` and `"true"` are invalid Tinker values and can cause an upstream `422`, returned here as `502`. Startup validation rejects these boolean strings. Even the valid value `"none"` produces HTTP `400` when the model does not support `reasoning_effort`. Leave `Tinker:ReasoningEffort` empty to omit the field; opt in only after confirming model support. If an environment variable (`Tinker__ReasoningEffort`) or user secret overrides this setting, clear that override and restart the API.

## Run the tests

From the project folder:

```powershell
dotnet test complexity-scorer-api.slnx
```

The tests simulate Tinker responses. They do not need a real Tinker key and do not make paid model calls. They check authentication, request validation, limits, score parsing, cancellation, and error handling.

A passing test run confirms the behavior against those simulated responses. A real request is still needed to verify access to your checkpoint and its model output.

## Advanced: matching the fine-tuned model

A fine-tuned model may expect the same instructions and message format used during training. This implementation sends a system instruction containing the complexity rubric, followed by the user's prompt. It expects Tinker to return only a number, such as `0.25`.

The exact Python `build_messages` and `parse_prediction` functions have not been provided. Set `Tinker:SystemPrompt` to the training instruction when available. If training also wrapped the user prompt in extra text, update message construction in [TinkerComplexityScorer.cs](Services/TinkerComplexityScorer.cs) to match it. Compare results against prompts with known expected scores before using them to route real traffic.

The supplied Python example used `enable_thinking=False`. This HTTP implementation omits `reasoning_effort` by default because support depends on the base model. The valid value `"none"` still produces HTTP `400` for models that do not support the parameter, returned here as `502`. Set `Tinker:ReasoningEffort` only for a model that supports it. **The HTTP setting has not been verified as equivalent to `enable_thinking=False` for this checkpoint.**

`Tinker:ReasoningEffort` controls that HTTP setting. An empty or null value omits it, but does not guarantee thinking is disabled; the provider documents a default effort of `0.9` when omitted. The separate `separate_reasoning: true` flag separates reasoning text from the final answer; it does not switch reasoning off. If an exact tokenizer template is required, the native Tinker sampling client may be needed.

The API rejects unfinished output, explanations, JSON-wrapped scores, and values outside `0` to `1`. It does not invent a fallback score. Correct response formatting does not guarantee that the score itself is accurate, including when a prompt tries to manipulate the model's rating.

See [Tinker's HTTP inference documentation](https://tinker-docs.thinkingmachines.ai/tinker/compatible-apis/openai/) for provider-specific behavior.

## Before deploying

Local setup gets the API running on your computer. For a hosted service, account for these differences:

- **Secrets:** keep both kinds of API keys in your hosting platform's secret store. Call this service from your application's backend so a public webpage does not expose its caller key.
- **HTTPS:** the API redirects HTTP to HTTPS when possible and otherwise rejects it. Set `AllowedHosts` to your actual hostnames.
- **Reverse proxies:** if another server accepts HTTPS and forwards requests to this app, configure ASP.NET Core to trust forwarded headers only from that known server. This is necessary before HTTPS redirection; trusting arbitrary forwarded headers is unsafe. The app does not trust them by default.
- **Multiple instances:** rate-limit counters live in each running copy of the app and reset on restart. Use a shared gateway or distributed limiter if you need one allowance across several servers. These limits do not enforce a permanent spending budget. Fixed windows can also allow bursts around their reset times.
- **Traffic protection:** use your hosting platform or gateway to handle large-scale abusive traffic before it reaches the API. Browser cross-origin access (CORS) is not enabled by default.
- **Privacy:** application code does not log prompts, keys, or raw Tinker responses, and responses tell clients not to cache them. Check any additional hosting logs or monitoring tools for secret exposure.
- **Provider behavior:** confirm Tinker's capacity and latency for your workload. Cancelling a local request cannot guarantee that Tinker stops work or billing for an already-started generation.

## Find your way around the code

| File or folder | What it does |
| --- | --- |
| [Program.cs](Program.cs) | Shows startup steps and the order in which requests pass through middleware |
| [Configuration/ConfigurationExtensions.cs](Configuration/ConfigurationExtensions.cs) | Loads and validates settings, including the Tinker key fallback |
| [Services/TinkerServiceExtensions.cs](Services/TinkerServiceExtensions.cs) | Registers the scoring service and configures its HTTP connection to Tinker |
| [Security/ApiSecurityExtensions.cs](Security/ApiSecurityExtensions.cs) | Registers API-key authentication, authorization, and rate limits |
| [Infrastructure/HttpConfigurationExtensions.cs](Infrastructure/HttpConfigurationExtensions.cs) | Sets up request timeouts, body-size limits, and standard JSON errors |
| [Infrastructure/HttpPipelineExtensions.cs](Infrastructure/HttpPipelineExtensions.cs) | Adds error handling, response headers, and HTTPS enforcement |
| [Endpoints/ComplexityEndpoint.cs](Endpoints/ComplexityEndpoint.cs) | Validates requests and returns scores or errors |
| [Services/TinkerComplexityScorer.cs](Services/TinkerComplexityScorer.cs) | Calls Tinker and validates its answer |
| [Configuration](Configuration) | Defines available settings and their defaults |
| [Security/ApiKeyAuthenticationHandler.cs](Security/ApiKeyAuthenticationHandler.cs) | Checks caller API keys |
| [tests/ComplexityScorer.Tests](tests/ComplexityScorer.Tests) | Contains the automated tests |
