// Negative control and reload behaviour. Without these, the assertions in verify-circuit.js could
// pass for the boring reason — "the page renders and nothing leaked because nothing happened".
//
// The control run is the one that matters: an unlaunched visitor must see the *default* name and
// must never send the vendor's token. If that passed while a launched visitor also passed, the
// launched assertions would be measuring nothing.
const { chromium } = require('playwright');
const {
    CONSOLE_URL, FAKE_CAMUS, TOKEN, BRAND, DATABASE, DEFAULT_BRAND,
    camusRequests, requestLaunchUrl, createReporter, runSuite,
} = require('./config');

const hasDatabase = db => document.body.innerText.includes(db);

runSuite(async () => {
    const { check, finish } = createReporter();
    const browser = await chromium.launch();

    // ---- Leg 1 is the door: it has to stay shut without the key ----
    {
        const post = (headers) => fetch(`${CONSOLE_URL}/api/console/sessions`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', ...headers },
            body: JSON.stringify({ brandName: BRAND }),
        });

        const noKey = await post({});
        const badKey = await post({ 'X-Console-Key': 'x'.repeat(36) });

        check('leg 1 rejects a missing key', noKey.status === 401, `got ${noKey.status}`);
        check('leg 1 rejects a wrong key', badKey.status === 401, `got ${badKey.status}`);
        check('leg 1 says nothing different for a wrong key than a missing one',
            JSON.stringify(await noKey.json()) === JSON.stringify(await badKey.json()));
    }

    // ---- A launch code is spent by the first visitor and no one else ----
    {
        const launchUrl = await requestLaunchUrl();
        const first = await fetch(launchUrl, { redirect: 'manual' });
        const second = await fetch(launchUrl, { redirect: 'manual' });

        check('launch code redeems once', first.status === 302, `got ${first.status}`);
        check('launch code cannot be replayed', second.status === 400, `got ${second.status}`);

        const cookie = first.headers.get('set-cookie') || '';
        check('launch sets an HttpOnly cookie', /httponly/i.test(cookie), cookie);
        check('launch cookie is SameSite=Lax', /samesite=lax/i.test(cookie), cookie);
        check('launch redirects to the console root', first.headers.get('location') === '/',
            String(first.headers.get('location')));
    }

    // ---- Control: a visitor who never went through a launch ----
    {
        const before = camusRequests().length;
        const context = await browser.newContext();
        const page = await context.newPage();

        await page.goto(CONSOLE_URL, { waitUntil: 'domcontentloaded' });
        await page.waitForTimeout(4000);

        const title = await page.title();
        const appBar = await page.locator('.app-bar-title').first().innerText().catch(() => '(missing)');
        const cookies = await context.cookies();
        const since = camusRequests().slice(before);

        check('control: unlaunched visitor sees the default name, not the vendor brand',
            title.trim() === DEFAULT_BRAND && appBar.trim() === DEFAULT_BRAND,
            `title=${JSON.stringify(title)} appBar=${JSON.stringify(appBar)}`);
        check('control: unlaunched visitor gets no launch cookie',
            !cookies.some(c => c.name.includes('cwc-launch')),
            JSON.stringify(cookies.map(c => c.name)));
        check('control: unlaunched visitor never sends the vendor token',
            !since.some(r => r.authorization === `Bearer ${TOKEN}`),
            `${since.length} new CamusDB requests`);

        await context.close();
    }

    // ---- Reload: one fresh handoff per page load ----
    {
        const launchUrl = await requestLaunchUrl();
        const context = await browser.newContext();
        const page = await context.newPage();

        await page.goto(launchUrl, { waitUntil: 'domcontentloaded' });
        await page.waitForFunction(hasDatabase, DATABASE, { timeout: 30000 });
        check('reload: first load applied the ticket', true);

        const before = camusRequests().length;
        await page.reload({ waitUntil: 'domcontentloaded' });

        let reapplied = true;
        try {
            await page.waitForFunction(hasDatabase, DATABASE, { timeout: 30000 });
        } catch {
            reapplied = false;
        }

        const appBar = await page.locator('.app-bar-title').first().innerText().catch(() => '(missing)');
        check('reload: brand survives a page reload', appBar.trim() === BRAND, `got ${JSON.stringify(appBar)}`);
        check('reload: ticket re-applied from a fresh handoff', reapplied);

        await page.waitForTimeout(1500);
        const since = camusRequests().slice(before);
        check('reload: token used again on the reloaded circuit',
            since.some(r => r.authorization === `Bearer ${TOKEN}`),
            `${since.length} new CamusDB requests`);

        // ---- The session is the cookie: drop it and the branding goes with it ----
        await context.clearCookies();
        await page.goto(CONSOLE_URL, { waitUntil: 'domcontentloaded' });
        await page.waitForTimeout(3000);

        const afterClear = await page.locator('.app-bar-title').first().innerText().catch(() => '(missing)');
        check('cookie removed: console reverts to the default name',
            afterClear.trim() === DEFAULT_BRAND, `got ${JSON.stringify(afterClear)}`);

        await context.close();
    }

    // ---- The allowlist governs the Configure dialog, not only a launch payload ----
    //
    // This is the wider of the two endpoint paths: leg 1 needs the vendor key, while the dialog is
    // open to whoever can load the page. The console's own process opens whatever it is given, so an
    // unguarded dialog reaches hosts the visitor cannot — which is what this asserts it does not.
    {
        const context = await browser.newContext();
        const page = await context.newPage();

        // Opening the dialog needs a live circuit, and a cold cache can cost this page one: the
        // Monaco editor script sometimes arrives after the circuit has already tried to use it,
        // which terminates the circuit and leaves a page whose buttons do nothing. A reload finds
        // the script cached. That is a fault of the console, not of the guard under test, so it is
        // retried here rather than allowed to report as a missing guard.
        const dialog = page.locator('.mud-dialog').last();
        let opened = false;

        for (let attempt = 0; attempt < 4 && !opened; attempt++) {
            await page.goto(CONSOLE_URL, { waitUntil: 'domcontentloaded' });
            await page.waitForTimeout(2500);
            await page.locator('.configure-btn').click({ timeout: 10000 }).catch(() => {});

            opened = await dialog.waitFor({ state: 'visible', timeout: 6000 })
                .then(() => true).catch(() => false);
        }

        check('configure: the dialog opens', opened);

        if (!opened) {
            await context.close();
            await browser.close();
            finish();
            return;
        }

        const endpoint = dialog.getByLabel('Endpoint', { exact: true });
        const connect = dialog.locator('.run-btn');

        const attempt = async (value) => {
            await endpoint.fill(value);
            await connect.click();
            await page.waitForTimeout(2500);

            return dialog.locator('.mud-alert-message').first()
                .innerText().catch(() => '');
        };

        const refused = await attempt('http://169.254.169.254');

        check('configure: an off-list endpoint is refused',
            /allowed endpoint list/i.test(refused), `alert was ${JSON.stringify(refused)}`);

        const allowed = await attempt(FAKE_CAMUS);

        check('configure: a listed endpoint is still accepted',
            !/allowed endpoint list/i.test(allowed), `alert was ${JSON.stringify(allowed)}`);

        await context.close();
    }

    await browser.close();
    finish();
});
