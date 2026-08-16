// Captures a branded console for eyeballing. Not an assertion — the screenshot is where the missing
// brand in the footer was spotted, which no DOM assertion had been written for at the time.
//
//   node screenshot.js [output.png]
const { chromium } = require('playwright');
const { DATABASE, requestLaunchUrl } = require('./config');

(async () => {
    const output = process.argv[2] || 'branded-console.png';
    const launchUrl = await requestLaunchUrl();

    const browser = await chromium.launch();
    const context = await browser.newContext({ viewport: { width: 1280, height: 520 } });
    const page = await context.newPage();

    await page.goto(launchUrl, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(db => document.body.innerText.includes(db), DATABASE, { timeout: 30000 });
    await page.waitForTimeout(1200);
    await page.screenshot({ path: output });

    await browser.close();
    console.log(`wrote ${output}`);
})();
