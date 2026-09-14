import { expect, test } from "@playwright/test";

test("renders the generated application", async ({ page }) => {
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Generated App" })).toBeVisible();
});