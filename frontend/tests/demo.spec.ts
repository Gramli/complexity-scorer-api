import { expect, test } from "@playwright/test";

const callerKey = "demo-caller-key-for-browser-tests-000000000";

test.beforeEach(async ({ page }) => {
  // Unhandled scoring calls fail locally; browser tests must never reach Tinker.
  await page.route("**/api/**", (route) => route.abort());
  await page.goto("/demo/");
});

test("submits the user’s exact custom prompt and displays the real response", async ({
  page,
}) => {
  const customPrompt =
    "  Analyze my own idea: balance cost, latency, and reliability.\nKeep these exact words.  ";
  let calls = 0;
  await page.route("**/api/complexity", async (route) => {
    calls++;
    expect(route.request().method()).toBe("POST");
    expect(route.request().postDataJSON()).toEqual({ prompt: customPrompt });
    expect(route.request().headers()["x-api-key"]).toBe(callerKey);
    await new Promise((resolve) => setTimeout(resolve, 250));
    await route.fulfill({ json: { score: 0.65 } });
  });

  await expect(
    page.getByRole("button", { name: "Analyze complexity" }),
  ).toBeDisabled();
  await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
  await page.getByLabel("What would you ask an AI?").fill(customPrompt);
  await page.getByRole("button", { name: "Analyze complexity" }).click();
  await expect(page.getByRole("button", { name: "Analyzing…" })).toBeDisabled();
  await expect(page.getByTestId("score")).toHaveText("0.65");
  await expect(
    page.getByRole("heading", { name: "Hard", exact: true }),
  ).toBeVisible();
  expect(calls).toBe(1);
  await page.screenshot({
    path: "test-results/demo-desktop.png",
    fullPage: true,
    animations: "disabled",
  });

  await page.locator(".inspector summary").click();
  expect(
    JSON.parse(await page.getByTestId("request-body").innerText()),
  ).toEqual({ prompt: customPrompt });
  expect(
    JSON.parse(await page.getByTestId("response-body").innerText()),
  ).toEqual({ score: 0.65 });
  await expect(page.locator(".inspector")).not.toContainText(callerKey);
  expect(
    await page.evaluate(() => ({
      local: localStorage.length,
      session: sessionStorage.length,
    })),
  ).toEqual({ local: 0, session: 0 });
  await page.reload();
  await expect(page.getByLabel("Caller API key", { exact: true })).toHaveValue(
    "",
  );
});

test("examples stay editable and keyboard submission sends the user’s edits", async ({
  page,
}) => {
  await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
  await page.getByRole("button", { name: /Extract a detail/ }).click();
  const field = page.getByLabel("What would you ask an AI?");
  await expect(field).toHaveValue(/alex@example.com/);
  const editedPrompt = "My own prompt, replacing the example completely.";
  await field.fill(editedPrompt);
  await page.route("**/api/complexity", async (route) => {
    expect(route.request().postDataJSON()).toEqual({ prompt: editedPrompt });
    await route.fulfill({ json: { score: 0 } });
  });
  await field.press("Control+Enter");
  await expect(page.getByTestId("score")).toHaveText("0.00");
  await field.fill("A different prompt");
  await expect(
    page.getByText("Prompt edited. Analyze again to update the score."),
  ).toBeVisible();
});

test("asks for a caller key before sending anything", async ({ page }) => {
  let calls = 0;
  await page.route("**/api/complexity", async (route) => {
    calls++;
    await route.abort();
  });
  await page.getByLabel("What would you ask an AI?").fill("My custom prompt");
  await page.getByRole("button", { name: "Analyze complexity" }).click();
  await expect(page.getByRole("alert")).toContainText("caller API key");
  await expect(
    page.getByLabel("Caller API key", { exact: true }),
  ).toBeFocused();
  expect(calls).toBe(0);
});

test("handles an invalid caller key without showing a score", async ({
  page,
}) => {
  await page.route("**/api/complexity", (route) =>
    route.fulfill({
      status: 401,
      json: { status: 401, title: "A valid X-Api-Key header is required." },
    }),
  );
  await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
  await page.getByLabel("What would you ask an AI?").fill("A test prompt");
  await page.getByRole("button", { name: "Analyze complexity" }).click();
  await expect(page.getByRole("alert")).toContainText("not accepted");
  await expect(page.getByTestId("score")).toHaveText("—");
  await expect(
    page.getByLabel("Caller API key", { exact: true }),
  ).toBeFocused();
});

test("honors Retry-After without automatically retrying", async ({ page }) => {
  let calls = 0;
  await page.route("**/api/complexity", async (route) => {
    calls++;
    await route.fulfill({
      status: 429,
      headers: { "Retry-After": "2" },
      json: { status: 429, title: "Too many requests." },
    });
  });
  await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
  await page.getByLabel("What would you ask an AI?").fill("A test prompt");
  await page.getByRole("button", { name: "Analyze complexity" }).click();
  await expect(
    page.getByRole("button", { name: /Try again in/ }),
  ).toBeDisabled();
  await expect(
    page.getByRole("button", { name: "Analyze complexity" }),
  ).toBeEnabled({ timeout: 5_000 });
  expect(calls).toBe(1);
});

for (const status of [502, 503, 504]) {
  test(`handles an upstream failure mapped to HTTP ${status}`, async ({
    page,
  }) => {
    await page.route("**/api/complexity", (route) =>
      route.fulfill({
        status,
        json: { status, title: "The scoring service request failed." },
      }),
    );
    await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
    await page.getByLabel("What would you ask an AI?").fill("A test prompt");
    await page.getByRole("button", { name: "Analyze complexity" }).click();
    await expect(page.getByRole("alert")).toBeVisible();
    await expect(page.getByTestId("score")).toHaveText("—");
    await expect(
      page.getByRole("button", { name: "Analyze complexity" }),
    ).toBeEnabled();
  });
}

test("rejects invalid scores and renders response text without executing markup", async ({
  page,
}) => {
  await page.route("**/api/complexity", (route) =>
    route.fulfill({
      json: {
        score: 99,
        content: '<img src=x onerror="window.demoInjected=true">',
      },
    }),
  );
  await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
  await page
    .getByLabel("What would you ask an AI?")
    .fill('<script>alert("custom prompt")</script>');
  await page.getByRole("button", { name: "Analyze complexity" }).click();
  await expect(page.getByRole("alert")).toContainText("unexpected score");
  await page.locator(".inspector summary").click();
  await expect(page.getByTestId("response-body")).toContainText("<img");
  await expect(page.locator(".inspector img, .inspector script")).toHaveCount(
    0,
  );
  expect(await page.evaluate(() => "demoInjected" in window)).toBe(false);
});

test("handles a network failure and lets the user submit again", async ({
  page,
}) => {
  await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
  await page.getByLabel("What would you ask an AI?").fill("A test prompt");
  await page.getByRole("button", { name: "Analyze complexity" }).click();
  await expect(page.getByRole("alert")).toContainText(
    "Could not reach the API",
  );
  await expect(
    page.getByRole("button", { name: "Analyze complexity" }),
  ).toBeEnabled();
});

test("fits a small screen and supports a complete mobile submission", async ({
  page,
}) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.route("**/api/complexity", (route) =>
    route.fulfill({ json: { score: 1 } }),
  );
  await page.getByLabel("Caller API key", { exact: true }).fill(callerKey);
  await page
    .getByLabel("What would you ask an AI?")
    .fill("A custom mobile prompt.");
  await page.getByRole("button", { name: "Analyze complexity" }).click();
  await expect(page.getByTestId("score")).toHaveText("1.00");
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
  await page.screenshot({
    path: "test-results/demo-mobile.png",
    fullPage: true,
    animations: "disabled",
  });
});
