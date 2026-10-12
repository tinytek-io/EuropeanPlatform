import { expect } from "@playwright/test";
import { test } from "@shared/e2e/fixtures/page-auth";
import { getBackOfficeBaseUrl } from "@shared/e2e/utils/constants";
import { createTestContext } from "@shared/e2e/utils/test-assertions";
import { step } from "@shared/e2e/utils/test-step-wrapper";

// The OIDC harness spec: signs in to the back-office through Authelia (a real, small open-source
// OIDC provider) instead of MockEasyAuth, proving the generic OIDC integration end-to-end —
// discovery, the authorization redirect, code + PKCE, the callback, and the groups claim that
// gates admin features. Only runs when the local Aspire stack has the harness enabled
// (AUTHELIA__ENABLED=true on the AppHost) and the runner opts in (BACK_OFFICE_OIDC_ENABLED=true);
// the default e2e suite keeps using the mock identity picker.
const OIDC_HARNESS_ENABLED = process.env.BACK_OFFICE_OIDC_ENABLED === "true";
const BACK_OFFICE_BASE_URL = getBackOfficeBaseUrl();

test.describe("back-office OIDC (Authelia harness)", () => {
  test.skip(
    !OIDC_HARNESS_ENABLED,
    "Set AUTHELIA__ENABLED=true on the AppHost and BACK_OFFICE_OIDC_ENABLED=true for the runner"
  );

  test("should sign in to the back-office through Authelia and render the dashboard", async ({
    ownerPage,
    browser
  }) => {
    // ownerPage provisions a tenant for this worker so the dashboard has data to render.
    createTestContext(ownerPage);
    const backOfficeContext = await browser.newContext({ baseURL: BACK_OFFICE_BASE_URL, ignoreHTTPSErrors: true });
    const page = await backOfficeContext.newPage();
    createTestContext(page);

    await step("Unauthenticated visit redirects to Authelia's login")(async () => {
      await page.goto(`${BACK_OFFICE_BASE_URL}/`);
      await expect(page.locator('input[name="username"]')).toBeVisible();
      await expect(page).toHaveURL(/localhost:9091/);
    })();

    await step("Log in as the harness admin (groups claim carries 'admins')")(async () => {
      await page.locator('input[name="username"]').fill("oidcadmin");
      await page.locator('input[name="password"]').fill("OIDCTest123!");
      await page.locator('button[type="submit"]').click();
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
