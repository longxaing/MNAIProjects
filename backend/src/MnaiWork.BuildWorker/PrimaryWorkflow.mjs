import { expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';

function containsValue(value, expected) {
  if (typeof value === 'string') return value.includes(expected);
  if (Array.isArray(value)) return value.some(item => containsValue(item, expected));
  return value !== null && typeof value === 'object'
    && Object.values(value).some(item => containsValue(item, expected));
}

export async function verifyPrimaryWorkflow(page, contract, target, apiBaseUrl = 'http://127.0.0.1:5000') {
  const unique = `mnai-${target}-${randomUUID()}`;
  const substitute = value => value.replaceAll('{{unique}}', unique);
  const expected = substitute(contract.expectedText);
  const apiOrigin = new URL(apiBaseUrl).origin;
  const matches = (response, method, path) => {
    const url = new URL(response.url());
    return url.origin === apiOrigin && url.pathname === path && response.request().method() === method;
  };
  const requireSuccess = async response => {
    if (!response.ok()) {
      throw new Error(`API ${response.request().method()} ${new URL(response.url()).pathname} returned ${response.status()}`);
    }
    return response;
  };
  try {
    for (const field of contract.fields) {
      await page.getByLabel(field.label, { exact: true }).fill(substitute(field.value), { timeout: 10000 });
    }
    const [mutation] = await Promise.all([
      page.waitForResponse(response => matches(response, contract.mutation.method, contract.mutation.path), { timeout: 15000 }),
      page.getByRole('button', { name: contract.submitButton, exact: true }).click({ timeout: 10000 }),
    ]);
    await requireSuccess(mutation);
    await mutation.finished();
    const pageUrl = page.url();
    await page.goto('about:blank');
    const [read] = await Promise.all([
      page.waitForResponse(response => matches(response, 'GET', contract.readPath), { timeout: 15000 }),
      page.goto(pageUrl, { waitUntil: 'domcontentloaded', timeout: 30000 }),
    ]);
    await requireSuccess(read);
    if (!containsValue(await read.json(), expected)) {
      throw new Error('Reloaded API data does not contain the newly submitted unique value. Check persistence and request/response contracts.');
    }
    await expect(page.getByText(expected, { exact: true }).filter({ visible: true }).first()).toBeVisible({ timeout: 10000 });
    console.log(`Primary workflow verified (${target}): ${contract.name}; ${contract.mutation.method} ${contract.mutation.path}; GET ${contract.readPath}; persisted value visible after reload.`);
  } catch (error) {
    throw new Error(`src/frontend/acceptance.json primary workflow failed (${target}): ${error.message}. Check UI event bindings, runtime API URL, API errors, persistence, and visible result rendering. Do not replace this check with heading-only assertions.`);
  }
}