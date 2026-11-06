import { expect, type Page } from "@playwright/test";
import { test } from "@shared/e2e/fixtures/page-auth";
import { getBackOfficeBaseUrl } from "@shared/e2e/utils/constants";
import { createTestContext } from "@shared/e2e/utils/test-assertions";
import { step } from "@shared/e2e/utils/test-step-wrapper";
import { existsSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";

// The staff identity harness spec: signs in to the back-office through authentik — the staff
// identity provider of the deployed environments, run locally from the same blueprints — instead
// of MockEasyAuth. It proves the passwordless sign-in end-to-end: discovery, the authorization
// redirect, the email one-time code (read from Mailpit), code + PKCE, the callback, and the groups
// claim that gates staff and admin features. Only runs when the local Aspire stack has the harness
// enabled (AUTHENTIK__ENABLED=true on the AppHost) and the runner opts in
// (BACK_OFFICE_OIDC_ENABLED=true); the default e2e suite keeps using the mock identity picker.
const OIDC_HARNESS_ENABLED = process.env.BACK_OFFICE_OIDC_ENABLED === "true";
const BACK_OFFICE_BASE_URL = getBackOfficeBaseUrl();
const DEV_ADMIN_EMAIL = "dev-admin@dev.localhost";

// Mailpit's HTTP API listens on the base port + 5 (PortAllocation.MailpitHttp).
function mailpitBaseUrl(): string {
  const portFile = join(resolve(__dirname, "..", "..", "..", "..", ".."), ".workspace", "port.txt");
  if (!existsSync(portFile)) {
    throw new Error("E2E tests require .workspace/port.txt to exist. Start Aspire AppHost first to bootstrap it.");
  }
  return `http://localhost:${Number.parseInt(readFileSync(portFile, "utf8").trim(), 10) + 5}`;
}

// Polls Mailpit for the newest message to the address and returns the six-digit sign-in code.
async function readSignInCode(page: Page, email: string): Promise<string> {
  const mailpit = mailpitBaseUrl();
  for (let attempt = 0; attempt < 30; attempt++) {
    const search = await page.request.get(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${email}"`)}`);
    const { messages } = (await search.json()) as { messages: { ID: string }[] };
    if (messages.length > 0) {
      const message = await page.request.get(`${mailpit}/api/v1/message/${messages[0].ID}`);
      const { Text, HTML } = (await message.json()) as { Text: string; HTML: string };
      const code = `${Text} ${HTML}`.match(/\b\d{6}\b/)?.[0];
      if (code) {
        return code;
      }
    }
    await page.waitForTimeout(1_000);
  }
  throw new Error(`No sign-in code arrived in Mailpit for ${email}.`);
}

test.describe("back-office staff sign-in (authentik harness)", () => {
  test.skip(
    !OIDC_HARNESS_ENABLED,
    "Set AUTHENTIK__ENABLED=true on the AppHost and BACK_OFFICE_OIDC_ENABLED=true for the runner"
  );

  test("should sign in to the back-office with an emailed code and render the dashboard", async ({
    ownerPage,
    browser
  }) => {
    // ownerPage provisions a tenant for this worker so the dashboard has data to render.
    createTestContext(ownerPage);
    const backOfficeContext = await browser.newContext({ baseURL: BACK_OFFICE_BASE_URL, ignoreHTTPSErrors: true });
    const page = await backOfficeContext.newPage();
    createTestContext(page);
    // Only codes sent during this test count.
    await page.request.delete(`${mailpitBaseUrl()}/api/v1/messages`);

    await step("Unauthenticated visit redirects to the staff sign-in")(async () => {
      await page.goto(`${BACK_OFFICE_BASE_URL}/`);
      await expect(page).toHaveURL(/auth\.dev\.localhost:9091/);
      await expect(page.locator('input[name="uidField"]')).toBeVisible();
    })();

    await step("Identify by email and confirm with the emailed code (no password)")(async () => {
      await page.locator('input[name="uidField"]').fill(DEV_ADMIN_EMAIL);
      await page.locator('input[name="uidField"]').press("Enter");
      const code = await readSignInCode(page, DEV_ADMIN_EMAIL);
      await page.locator('input[name="code"]').fill(code);
      await page.locator('input[name="code"]').press("Enter");
    })();

    await step("Callback returns to the back-office dashboard with an admin identity")(async () => {
      await expect(page.getByRole("heading", { name: "Dashboard" })).toBeVisible();
      await expect(page).toHaveURL(new RegExp(`^${BACK_OFFICE_BASE_URL.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`));
      // The admin group gates the tenant detail entry points; the accounts list proves the
      // groups claim arrived and the identity carries admin rights.
      await page.goto(`${BACK_OFFICE_BASE_URL}/accounts`);
      await expect(page.getByRole("table", { name: "Accounts" })).toBeVisible();
    })();
  });
});
