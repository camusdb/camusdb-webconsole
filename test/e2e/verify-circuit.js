// Drives a real browser through the vendor handoff and checks the two things an HTTP client cannot:
//
//   1. the Blazor circuit spends the single-use handoff and applies the launch ticket, and
//   2. the access token reaches CamusDB while staying unreachable from the browser.
//
// (1) is asserted through the database chip. The ticket names a database the *prerender* pass cannot
// know — it renders whatever appsettings configured — so that name appearing in the live DOM is only
// possible if the interactive circuit redeemed the handoff and ran ApplyLaunchTicket.
const { chromium } = require('playwright');
const {
    CONSOLE_URL, FAKE_CAMUS, TOKEN, BRAND, DATABASE,
    camusRequests, requestLaunchUrl, createReporter, runSuite,
} = require('./config');

runSuite(async () => {
    const { check, finish } = createReporter();

    const launchUrl = await requestLaunchUrl();
    check('leg 1 returns a launch url', launchUrl.includes('code='), launchUrl);

    const browser = await chromium.launch();
    const context = await browser.newContext();
    const page = await context.newPage();

    const network = [];
    page.on('request', r => network.push(`${r.method()} ${r.url()}`));

    // Taken from the browser's own document response rather than a second fetch: the launch code is
    // single use, so fetching it here would spend it and leave the browser with an expired link.
    let prerendered = '';
    page.on('response', async r => {
        if (r.request().resourceType() === 'document') {
            try { prerendered += await r.text(); } catch { /* redirect bodies are empty */ }
        }
    });

    // ---- Leg 2: the visitor's browser ----
    await page.goto(launchUrl, { waitUntil: 'domcontentloaded' });

    let applied = true;
    try {
        await page.waitForFunction(db => document.body.innerText.includes(db), DATABASE, { timeout: 30000 });
    } catch {
        applied = false;
    }

    const title = await page.title();
    const appBar = await page.locator('.app-bar-title').first().innerText().catch(() => '(missing)');
    const footer = await page.locator('.console-footer span').first().innerText().catch(() => '(missing)');
    const bodyText = await page.locator('body').innerText();
    const html = await page.content();

    check('app bar shows the vendor brand', appBar.trim() === BRAND, `got ${JSON.stringify(appBar)}`);
    check('tab title shows the vendor brand', title.trim() === BRAND, `got ${JSON.stringify(title)}`);
    check('footer shows the vendor brand', footer.trim() === BRAND, `got ${JSON.stringify(footer)}`);

    check('CIRCUIT APPLIED THE TICKET (vendor database is live in the DOM)', applied,
        `"${DATABASE}" never appeared; body was:\n        ${bodyText.slice(0, 300).replace(/\n/g, ' ')}`);
    check('vendor endpoint applied', bodyText.includes(FAKE_CAMUS.replace(/^https?:\/\//, '')),
        `footer did not mention ${FAKE_CAMUS}`);

    // ---- the token must be unreachable from the browser ----
    const storage = await page.evaluate(() => ({
        cookie: document.cookie,
        local: JSON.stringify(localStorage),
        session: JSON.stringify(sessionStorage),
    }));
    const cookies = await context.cookies();

    check('token absent from live DOM/HTML', !html.includes(TOKEN));
    check('token absent from prerendered HTML', !prerendered.includes(TOKEN));
    check('token absent from document.cookie', !storage.cookie.includes(TOKEN));
    check('token absent from localStorage', !storage.local.includes(TOKEN));
    check('token absent from sessionStorage', !storage.session.includes(TOKEN));
    check('token absent from every request the browser made', !network.some(u => u.includes(TOKEN)));
    check('token absent from all cookie values', !cookies.some(c => c.value.includes(TOKEN)));

    const launchCookie = cookies.find(c => c.name.includes('cwc-launch'));
    check('launch cookie exists', !!launchCookie, JSON.stringify(cookies.map(c => c.name)));
    check('launch cookie is HttpOnly', !!launchCookie && launchCookie.httpOnly === true);
    check('launch cookie is invisible to script', !storage.cookie.includes('cwc-launch'),
        `document.cookie = ${JSON.stringify(storage.cookie)}`);

    // ---- and it must have reached CamusDB ----
    await page.waitForTimeout(1500);
    const requests = camusRequests();
    const authorized = requests.filter(r => r.authorization);

    check('CamusDB was reached at the vendor-supplied endpoint', requests.length > 0,
        `${requests.length} requests recorded`);
    check('TOKEN REACHED CAMUSDB as Authorization: Bearer',
        authorized.some(r => r.authorization === `Bearer ${TOKEN}`),
        `auth headers seen: ${JSON.stringify(authorized.map(r => `${r.url} -> ${r.authorization}`), null, 2)}`);

    console.log('\n--- what the stand-in CamusDB saw ---');
    for (const r of requests.slice(0, 6)) {
        const auth = r.authorization ? `${r.authorization.slice(0, 28)}…` : '(none)';
        console.log(`  ${r.method} ${r.url}  auth=${auth}`);
    }

    await browser.close();
    finish();
});
