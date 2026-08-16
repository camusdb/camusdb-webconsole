// A stand-in CamusDB that answers /ping and records the Authorization header of every request.
//
// That header is the whole point. The driver sends a supplied access token as
// `Authorization: Bearer <token>`, so a request arriving here with the vendor's token in it is the
// proof that the token handed to the console's backend reached CamusDB traffic — having never been
// anywhere the browser could read it.
//
// It deliberately does NOT implement the result-set wire format. The console will report
// "Disconnected" with a type error after the first query, which is expected and irrelevant: the
// token has already been sent by then. Point the suites at a real server if you want a green
// connection too.
const http = require('http');
const fs = require('fs');
const path = require('path');

const LOG = process.argv[2] || path.join(__dirname, '.camus-requests.log');
const PORT = Number(process.argv[3] || 5399);

const seen = [];

const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => (body += chunk));
    req.on('end', () => {
        seen.push({
            method: req.method,
            url: req.url,
            authorization: req.headers['authorization'] || null,
            body: body.slice(0, 400),
        });
        fs.writeFileSync(LOG, JSON.stringify(seen, null, 2));

        if (req.url === '/ping' || req.url === '/health') {
            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({ status: 'ok', dateTime: new Date().toISOString() }));
            return;
        }

        if (req.url.startsWith('/execute-sql-query')) {
            res.writeHead(200, { 'content-type': 'application/json' });
            res.end(JSON.stringify({
                status: 'ok',
                columns: [{ name: 'database', type: 'String' }],
                rows: [[{ type: 'String', strValue: 'analytics' }]],
            }));
            return;
        }

        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ status: 'ok' }));
    });
});

server.listen(PORT, '127.0.0.1', () => console.log(`fake camusdb listening on ${PORT}, logging to ${LOG}`));
