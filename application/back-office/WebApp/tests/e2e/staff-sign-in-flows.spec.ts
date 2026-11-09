import { expect, type Page } from "@playwright/test";
import { test } from "@shared/e2e/fixtures/page-auth";
import { getBackOfficeBaseUrl } from "@shared/e2e/utils/constants";
import { createTestContext } from "@shared/e2e/utils/test-assertions";
import { step } from "@shared/e2e/utils/test-step-wrapper";
import { existsSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";

// The real staff sign-in: the back-office signs staff in itself with an emailed one-time code,
// read here from Mailpit, against the AppHost's development staff identity file. Only runs when
// the local Aspire stack has it enabled (STAFFIDENTITY__ENABLED=true on the AppHost) and the
// runner opts in (STAFF_SIGN_IN_ENABLED=true); the default e2e suite keeps using the mock picker.
const STAFF_SIGN_IN_ENABLED = process.env.STAFF_SIGN_IN_ENABLED === "true";
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

// Polls Mailpit for the newest message to the address and returns its six-letter sign-in code.
async function readSignInCode(page: Page, email: string): Promise<string> {
  const mailpit = mailpitBaseUrl();
  for (let attempt = 0; attempt < 30; attempt++) {
    const search = await page.request.get(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:"${email}"`)}`);
    const { messages } = (await search.json()) as { messages: { ID: string }[] };
    if (messages.length > 0) {
      const message = await page.request.get(`${mailpit}/api/v1/message/${messages[0].ID}`);
      const { Text } = (await message.json()) as { Text: string };
      const code = Text.match(/is: ([A-Z]{6})/)?.[1];
      if (code) {
        return code;
      }
    }
    await page.waitForTimeout(1_000);
  }
  throw new Error(`No sign-in code arrived in Mailpit for ${email}.`);
}

test.describe("@comprehensive", () => {
  test.skip(
    !STAFF_SIGN_IN_ENABLED,
    "Set STAFFIDENTITY__ENABLED=true on the AppHost and STAFF_SIGN_IN_ENABLED=true for the runner"
  );

  /**
   * Signs in to the back-office as a staff member listed in the development staff identity file:
   * the unauthenticated redirect to the sign-in page, the emailed one-time code (no password), the
   * session, and the admin group that gates the accounts list.
   */
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

    await step("Open the back-office unauthenticated & verify the staff sign-in page")(async () => {
      await page.goto(`${BACK_OFFICE_BASE_URL}/`);

      await expect(page.getByRole("heading", { name: "Back Office - Sign in" })).toBeVisible();
    })();

    await step("Request a sign-in code by email & verify the code entry")(async () => {
      await page.getByLabel("Email").fill(DEV_ADMIN_EMAIL);
      await page.getByRole("button", { name: "Send sign-in code" }).click();

      await expect(page.getByRole("heading", { name: "Enter your sign-in code" })).toBeVisible();
    })();

    await step("Enter the emailed code & verify the dashboard with an admin identity")(async () => {
      const code = await readSignInCode(page, DEV_ADMIN_EMAIL);
      await page.getByLabel("Sign-in code").fill(code);
      await page.getByRole("button", { name: "Sign in" }).click();

      await expect(page.getByRole("heading", { name: "Dashboard" })).toBeVisible();
      // The admin group gates the accounts list: it proves the session carries admin rights.
      await page.goto(`${BACK_OFFICE_BASE_URL}/accounts`);
      await expect(page.getByRole("table", { name: "Accounts" })).toBeVisible();
    })();
  });
});
