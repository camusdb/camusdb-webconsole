// Shared settings and assertion helpers for the vendor-launch end-to-end suites.
const fs = require('fs');
const path = require('path');

const CONSOLE_URL = process.env.CONSOLE_URL || 'http://127.0.0.1:5320';
const CONSOLE_KEY = process.env.CONSOLE_KEY || '0123456789abcdef0123456789abcdef0123';
const FAKE_CAMUS = process.env.FAKE_CAMUS || 'http://127.0.0.1:5399';
const CAMUS_LOG = process.env.CAMUS_LOG || path.join(__dirname, '.camus-requests.log');

// Distinctive on purpose: every leak assertion greps for this exact string, so a partial match in
// the page or in storage is still a failure worth reading.
const TOKEN = 'eyJhbGciOiJIUzI1NiJ9.SUPER-SECRET-VENDOR-TOKEN-DO-NOT-LEAK.sig';
const BRAND = 'Acme Data Console';
const DATABASE = 'analytics';
const DEFAULT_BRAND = 'CamusDB Web Console';

/** Requests the stand-in CamusDB has recorded so far. */
function camusRequests() {
    try {
        return JSON.parse(fs.readFileSync(CAMUS_LOG, 'utf8'));
    } catch {
        return [];
    }
}

/** Leg 1 of the handoff: the vendor's backend asking for a launch link. No browser involved. */
async function requestLaunchUrl(overrides = {}) {
    const response = await fetch(`${CONSOLE_URL}/api/console/sessions`, {
        method: 'POST',
        headers: { 'X-Console-Key': CONSOLE_KEY, 'Content-Type': 'application/json' },
        body: JSON.stringify({
            brandName: BRAND,
            accessToken: TOKEN,
            database: DATABASE,
            endpoint: FAKE_CAMUS,
            ...overrides,
        }),
    });

    const body = await response.json();

    if (!response.ok || !body.launchUrl)
        throw new Error(`leg 1 failed (${response.status}): ${JSON.stringify(body)}`);

    return body.launchUrl;
}

/** Minimal check/report harness — the suites are short enough not to need a runner. */
function createReporter() {
    let failures = 0;

    return {
        check(label, ok, detail) {
            if (!ok) failures++;
            console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}${ok || !detail ? '' : `\n        ${detail}`}`);
        },
        finish() {
            console.log(`\n${failures === 0 ? 'ALL PASS' : `${failures} FAILURE(S)`}`);
            process.exit(failures === 0 ? 0 : 1);
        },
    };
}

/**
 * Runs a suite so that a thrown error reads as a failure rather than an unhandled rejection stack.
 * A suite that cannot even start — a refused launch, an unreachable console — is a failure of the
 * thing under test, and should look like one.
 */
function runSuite(suite) {
    suite().catch(error => {
        console.log(`\nFAIL  the suite could not run\n        ${error.message}`);
        console.log('\n1 FAILURE(S)');
        process.exit(1);
    });
}

module.exports = {
    CONSOLE_URL, CONSOLE_KEY, FAKE_CAMUS, CAMUS_LOG,
    TOKEN, BRAND, DATABASE, DEFAULT_BRAND,
    camusRequests, requestLaunchUrl, createReporter, runSuite,
};
