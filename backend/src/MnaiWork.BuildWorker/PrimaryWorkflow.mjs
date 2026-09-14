import { expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';

export async function verifyAppliedStyles(page, target) {
  const evidence = await page.evaluate(async () => {
    const viewport = document.querySelector('meta[name="viewport"]')?.content ?? '';
    if (!/width\s*=\s*device-width/i.test(viewport)) return { error: 'Missing device-width viewport metadata' };
    const frame = document.createElement('iframe');
    frame.setAttribute('aria-hidden', 'true');
    frame.style.cssText = `position:fixed;left:-20000px;top:0;width:${innerWidth}px;height:${innerHeight}px;visibility:hidden`;
    const ready = new Promise(resolve => frame.addEventListener('load', resolve, { once: true }));
    frame.srcdoc = '<!doctype html><html><body></body></html>';
    document.body.append(frame);
    try {
      await ready;
      const baseline = frame.contentDocument;
      const clone = document.body.cloneNode(true);
      clone.querySelectorAll('script,style,link,iframe,img,video,audio,source,object,embed').forEach(node => node.remove());
      for (const node of [clone, ...clone.querySelectorAll('*')]) {
        node.removeAttribute('style');
        for (const attribute of [...node.attributes]) if (attribute.name.startsWith('on')) node.removeAttribute(attribute.name);
      }
      baseline.body.replaceWith(baseline.adoptNode(clone));
      const differences = (element, plain, properties) => properties.filter(property =>
        getComputedStyle(element).getPropertyValue(property) !== frame.contentWindow.getComputedStyle(plain).getPropertyValue(property));
      const layouts = ['body', 'main', 'form'];
      const layoutChanges = layouts.flatMap(selector => {
        const element = document.querySelector(selector), plain = baseline.querySelector(selector);
        return element && plain ? differences(element, plain, ['display', 'padding-top', 'padding-left', 'max-width', 'background-color', 'font-family']) : [];
      });
      const controls = [...document.querySelectorAll('button,input,textarea,select')];
      const plainControls = [...baseline.querySelectorAll('button,input,textarea,select')];
      const styledControls = controls.filter((element, index) => element.getBoundingClientRect().width > 0 && plainControls[index]
        && differences(element, plainControls[index], ['background-color', 'border-radius', 'border-top-color', 'padding-top', 'padding-left', 'min-height']).length >= 2).length;
      return { layoutChanges: [...new Set(layoutChanges)], styledControls };
    } finally {
      frame.remove();
    }
  });
  if (evidence.error || evidence.layoutChanges.length < 2 || evidence.styledControls < 1) {
    throw new Error(`Applied UI styles failed (${target}): ${JSON.stringify(evidence)}. Import the product stylesheet, verify built assets and responsive viewport, and style the layout and primary controls. CSS filenames or className alone are not applied styles. This is a baseline check, not an aesthetic score.`);
  }
  console.log(`Applied UI styles verified (${target}): ${JSON.stringify(evidence)}`);
}

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