/*
 * A Monaco language for CamusDB SQL.
 *
 * Monaco's built-in "sql" mode is T-SQL flavored: it misses SHOW, TABLES, DATABASES, STRING, OID,
 * INT64, BOOL, ILIKE, BRANCH, GRANTS and a couple of dozen more, and it highlights plenty that
 * CamusDB does not accept. The word lists below are derived from the server's own lexer
 * (CamusDB.Core/SQLParser/SQLParser.Language.analyzer.lex) and scalar-function registry, so the
 * editor colors exactly what the engine parses — no more and no less.
 *
 * Deliberately absent: FULL [OUTER] JOIN. The lexer has no token for FULL.
 *
 * UNION, INTERSECT and EXCEPT are reserved words for the set operations. The ALL after one of them is
 * a plain identifier, so it is colored only in that phrase (UNION ALL).
 *
 * LEFT, RIGHT, OUTER and CROSS are reserved words for LEFT [OUTER] JOIN, RIGHT [OUTER] JOIN and
 * CROSS JOIN. RETURNING is reserved too: it starts the RETURNING list of an INSERT. A column with
 * one of these names must be backticked.
 *
 * NUMERIC and DECIMAL are one type token. `NUMERIC '1.5'` is the typed literal, so it colors as a
 * type followed by a string.
 *
 * ANY, SOME and ALL are not keywords either. The engine parses a quantified comparison
 * (`x = ANY (SELECT ...)`) as a call to a function of that name, so they are colored as operators
 * only when a '(' follows. A bare `all` stays a column name.
 *
 * Also absent, and for the same reason: STATISTICS (in SHOW STATISTICS) and the view modifiers
 * REPLACE, CASCADE, RESTRICT, CASCADED, LOCAL, OPTION, OWNER, CONCURRENTLY. The lexer has no token
 * for any of them. Each arrives as a plain identifier and the parse action checks it, so each stays
 * usable as a table or column name. Do not promote one to a keyword here.
 *
 * Such a word is colored only inside the phrase where the grammar checks it: SHOW SLOW QUERIES,
 * SET CLUSTER SETTING, CREATE OR REPLACE, FLUSH SESSIONS and the rest of the `phrases` state. Each
 * phrase starts at a reserved word or has a shape no other statement takes, so a column named
 * `queries` or `cascade` stays a plain name everywhere else.
 *
 * The sequence statements follow the same rule. SEQUENCE and SEQUENCES are the only new tokens.
 * INCREMENT, MINVALUE, MAXVALUE, NO, CYCLE, CACHE, RESTART, OWNED, IDENTITY, GENERATED, ALWAYS,
 * SERIAL and CONTINUE are plain identifiers that the parse action checks.
 *
 * Foreign keys add four reserved words, FOREIGN, REFERENCES, DEFERRABLE and INITIALLY, plus NO ACTION,
 * which the lexer matches as ONE token across the two words (TNoAction). A lone NO stays an
 * identifier, so it is colored only when ACTION follows. MATCH, SIMPLE, FULL, PARTIAL, RESTRICT,
 * CASCADE, DEFERRED and IMMEDIATE are plain identifiers that the parse action checks. Do not promote
 * one to a keyword here.
 *
 * OVER is reserved, for window functions. The other window words, PARTITION, ROWS, RANGE, UNBOUNDED,
 * PRECEDING, FOLLOWING, CURRENT and ROW, are plain identifiers, so they are colored only inside the
 * window phrases: PARTITION BY, ROWS BETWEEN, UNBOUNDED PRECEDING, CURRENT ROW, 2 PRECEDING. GROUPS and
 * EXCLUDE parse only to be refused, so they stay plain.
 *
 * JSONB and its alias JSON are not reserved either: a table or a column can be named json. So the two
 * words are colored as a type only where a type goes: after a column name in a column definition,
 * after '::', after AS in a CAST, and before a string in a typed literal (JSONB '{}'). The index forms
 * USING GIN, CREATE INVERTED INDEX and the operator classes jsonb_ops and jsonb_path_ops are plain
 * identifiers that the parse action checks, so they are phrases too.
 *
 * The jsonb operators are ->, ->>, #>, #>>, #-, @>, <@, ?, ?| and ?&. '<@' is the operator only when
 * no name character follows it: `x<@p` is still `x < @p`, as in the lexer.
 *
 * BIGINT is another spelling of INT64, and it is not reserved either. So it is colored as a type only
 * where a type goes, as JSONB is. DOUBLE PRECISION is one lexer token across the two words, so
 * PRECISION alone stays a name.
 *
 * INTERVAL is a keyword only before its literal: the lexer returns TINTERVAL only when a quote, a
 * digit or a parameter follows the word (INTERVAL '1 day', INTERVAL '3' HOUR, INTERVAL 3 HOUR,
 * INTERVAL @n DAY). Anywhere else the word is a name, so a column named interval keeps working. The
 * unit after the value is a plain identifier that the parse action checks (IntervalMath.TryParseUnit),
 * so it is colored only in that phrase. An interval is a value of an expression only: a table cannot
 * store it. So interval is colored as a type after '::' and after AS in a CAST, and never in a column
 * definition.
 *
 * AGE is a function, but age is also a common column name. So AGE is colored only when a '(' follows.
 *
 * Inline functions and triggers: CREATE [OR REPLACE] FUNCTION f(a INT64) RETURNS STRING LANGUAGE
 * csharp AS $$ body $$, and CREATE TRIGGER t ON tbl BEFORE INSERT LANGUAGE csharp AS $$ body $$. No
 * word of these statements is reserved: FUNCTION, FUNCTIONS, TRIGGERS, RETURNS, CALLED, INPUT and
 * LANGUAGE are plain identifiers that the parse action checks. So they are phrases too.
 *
 * The body is dollar-quoted text: $$ … $$ or $tag$ … $tag$, where the tag is an ASCII identifier. The
 * text is raw up to the same quote, with no escapes. The only language is csharp, so the body is
 * colored by Monaco's C# tokenizer. One difference from the lexer: Monarch makes every rule regex
 * case-insensitive here, so $Fn$ also closes $fn$ in the editor. The lexer compares the tags exactly.
 *
 * Registered as its own language id rather than by overriding "sql": Monaco loads a basic language's
 * tokenizer lazily, on first use, so replacing the "sql" provider at page load races with that import
 * and can be silently overwritten.
 */
(function () {
    'use strict';

    const LANGUAGE_ID = 'camussql';

    const definition = {
        defaultToken: '',

        // '.sql', not '.camussql', even though the language id differs: Monaco's themes key their
        // rules on this postfix, and the only rules for functions and word operators are
        // `predefined.sql` (magenta) and `operator.sql` (slate). A '.camussql' postfix matches
        // neither, so those tokens would silently render as plain text.
        tokenPostfix: '.sql',

        ignoreCase: true,

        brackets: [
            { open: '[', close: ']', token: 'delimiter.square' },
            { open: '(', close: ')', token: 'delimiter.parenthesis' },
        ],

        keywords: [
            'ADD', 'ALTER', 'ANALYZE', 'ANCESTORS', 'AS', 'ASC',
            'BEGIN', 'BRANCH', 'BRANCHES', 'BY', 'CASE', 'CAST',
            'CHECK', 'COLUMN', 'COLUMNS', 'COMMENT', 'COMMIT', 'CONSTRAINT',
            'CREATE', 'CROSS', 'DATABASE', 'DATABASES', 'DEFAULT', 'DEFERRABLE',
            'DELETE', 'DESC', 'DESCRIBE', 'DISTINCT', 'DROP', 'ELSE',
            'END', 'ESCAPE', 'EVICT', 'EXCEPT', 'EXPLAIN', 'FALSE', 'FOR',
            'FORCE', 'FOREIGN', 'FROM', 'GRANT', 'GRANTS', 'GROUP',
            'HAVING', 'IDENTIFIED', 'IF', 'INCLUDE', 'INDEX', 'INDEXES',
            'INITIALLY', 'INNER', 'INSERT', 'INTERSECT', 'INTO', 'JOIN',
            'KEY', 'LEFT', 'LIMIT', 'MATERIALIZED', 'NULL', 'OFFSET',
            'ON', 'ORDER', 'ORPHAN', 'OUTER', 'OVER', 'PRIMARY',
            'PRIVILEGES', 'REFERENCES', 'REFRESH', 'RELINK', 'RENAME', 'RESET',
            'RETURNING', 'REVOKE', 'RIGHT', 'ROLLBACK', 'SELECT', 'SEQUENCE',
            'SEQUENCES', 'SET', 'SHOW', 'START', 'TABLE', 'TABLES',
            'THEN', 'TO', 'TRANSACTION', 'TRUE', 'TRUNCATE', 'UNION',
            'UNIQUE', 'UPDATE', 'USER', 'VALUES', 'VIEW', 'VIEWS',
            'WHEN', 'WHERE', 'WITH', 'WITHOUT',
        ],

        operators: [
            'AND', 'BETWEEN', 'EXISTS', 'ILIKE', 'IN', 'IS',
            'LIKE', 'NOT', 'OR',
        ],

        builtinTypes: [
            'ARRAY', 'BLOB', 'BOOL', 'BOOLEAN', 'BYTES', 'CHAR',
            'DATE', 'DATETIME', 'DECIMAL', 'DOUBLE', 'FLOAT', 'FLOAT32',
            'FLOAT64', 'GUID', 'INT', 'INT64', 'INTEGER', 'NUMERIC',
            'OBJECT_ID', 'OID', 'REAL', 'SMALLINT', 'STRING', 'TEXT',
            'TIMESTAMP', 'UUID', 'VARCHAR',
        ],

        // Registered scalar functions plus the five aggregates. Aliases count: the registry resolves
        // CEILING/POWER/NVL/NOW/STR_ID to the same descriptors as CEIL/POW/IFNULL/CURRENT_TIMESTAMP/
        // TO_ID, so leaving them out colored a call the engine accepts as a plain identifier.
        //
        // COSINE_DISTANCE, INNER_PRODUCT, L2_DISTANCE, OCTET_LENGTH and VECTOR_DIMS come from
        // VectorScalarFunctions. They measure a vector held in a BYTES column.
        //
        // CARDINALITY, ARRAY_LENGTH and ARRAY_CONTAINS come from ArrayScalarFunctions, MD5 and the
        // SHA functions from HashScalarFunctions, and NEXTVAL, CURRVAL, LASTVAL and SETVAL from
        // SequenceScalarFunctions.
        //
        // ScalarFunctionRegistry.CreateDefault() lists the other names: the bytes, hash, clock, date
        // constructor and date pattern functions (HEX, DIGEST, CLOCK_TIMESTAMP, MAKE_DATE, TO_CHAR, ...)
        // and the MySQL date-part shortcuts (YEAR, MONTH, DAYOFWEEK, ...). ALL, ANY and SOME are
        // registered too, but they are colored as quantifiers. See the header comment.
        //
        // POSITION(needle IN haystack) is not registered. The grammar rewrites it to STRPOS. EXTRACT is
        // registered, and the grammar also accepts its EXTRACT(field FROM x) form. GENERATE_SERIES is
        // the one table function: it is called in FROM (TableFunctionRegistry).
        //
        // ROW_NUMBER, RANK, DENSE_RANK, PERCENT_RANK, CUME_DIST, NTILE, LAG, LEAD, FIRST_VALUE,
        // LAST_VALUE and NTH_VALUE are the window functions. They are valid only with OVER.
        //
        // The JSONB_* and JSON_* document functions come from JsonbScalarFunctions, JSONB_AGG and
        // JSONB_OBJECT_AGG from JsonbAggregates, and JSONB_EACH, JSONB_OBJECT_KEYS and
        // JSONB_ARRAY_ELEMENTS from JsonbTableFunctions (called in FROM). A JSON_* spelling is listed
        // only where the server registers it. JSON_VALID, JSON_TYPE, JSON_EXTRACT, JSON_VALUE,
        // JSON_CONTAINS and JSON_ARRAY_LENGTH are the older MySQL-style functions on text.
        //
        // MAKE_INTERVAL, JUSTIFY_DAYS, JUSTIFY_HOURS, JUSTIFY_INTERVAL and DATE_BIN come from
        // IntervalScalarFunctions, and DATE_SUB from DateTimeScalarFunctions. AGE is registered too,
        // but it is colored only before a '(' (see the header comment), so it is not in this list.
        //
        // COUNT_DISTINCT, GROUP_CONCAT, STRING_AGG and ARRAY_AGG are absent on purpose. The engine names
        // them only to reject them (AggregateFunctionCatalog.ReservedOnly). No aggregator implements them.
        builtinFunctions: [
            'ABS', 'ARRAY_CONTAINS', 'ARRAY_LENGTH', 'AVG', 'BIT_COUNT', 'BIT_LENGTH',
            'CARDINALITY', 'CEIL', 'CEILING', 'CLOCK_TIMESTAMP', 'COALESCE', 'CONCAT',
            'CONTAINS', 'CONVERT_FROM', 'CONVERT_TO', 'COSINE_DISTANCE', 'COUNT', 'CRC32',
            'CUME_DIST', 'CURRENT_DATABASE', 'CURRENT_DATE', 'CURRENT_ROLE', 'CURRENT_TIMESTAMP', 'CURRENT_USER',
            'CURRVAL', 'DATE_ADD', 'DATE_BIN', 'DATE_DIFF', 'DATE_FORMAT', 'DATE_PART', 'DATE_SUB', 'DATE_TRUNC',
            'DAY', 'DAYNAME', 'DAYOFMONTH', 'DAYOFWEEK', 'DAYOFYEAR', 'DECODE',
            'DENSE_RANK', 'DIGEST', 'ENCODE', 'ENDS_WITH', 'EXTRACT', 'FIRST_VALUE',
            'FLOOR', 'FROM_BASE64', 'FROM_UNIXTIME', 'GENERATE_SERIES', 'GEN_ID', 'GEN_RANDOM_BYTES',
            'GEN_UUID_V4', 'GEN_UUID_V7', 'GET_BIT', 'GET_BYTE', 'HEX', 'HMAC',
            'HOUR', 'IFNULL', 'INNER_PRODUCT', 'IS_SUPERUSER', 'JSONB_AGG', 'JSONB_ARRAY_ELEMENTS',
            'JSONB_ARRAY_ELEMENTS_TEXT', 'JSONB_ARRAY_LENGTH', 'JSONB_BUILD_ARRAY', 'JSONB_BUILD_OBJECT', 'JSONB_CONTAINED', 'JSONB_CONTAINS',
            'JSONB_DELETE_PATH', 'JSONB_EACH', 'JSONB_EACH_TEXT', 'JSONB_EXISTS', 'JSONB_EXISTS_ALL', 'JSONB_EXISTS_ANY',
            'JSONB_EXTRACT_PATH', 'JSONB_EXTRACT_PATH_TEXT', 'JSONB_INSERT', 'JSONB_OBJECT', 'JSONB_OBJECT_AGG', 'JSONB_OBJECT_KEYS',
            'JSONB_PRETTY', 'JSONB_SET', 'JSONB_SET_LAX', 'JSONB_STRIP_NULLS', 'JSONB_TYPEOF', 'JSON_AGG',
            'JSON_ARRAY_ELEMENTS', 'JSON_ARRAY_ELEMENTS_TEXT', 'JSON_ARRAY_LENGTH', 'JSON_BUILD_ARRAY', 'JSON_BUILD_OBJECT', 'JSON_CONTAINS',
            'JSON_EACH', 'JSON_EACH_TEXT', 'JSON_EXTRACT', 'JSON_EXTRACT_PATH', 'JSON_EXTRACT_PATH_TEXT', 'JSON_OBJECT',
            'JSON_OBJECT_AGG', 'JSON_OBJECT_KEYS', 'JSON_STRIP_NULLS', 'JSON_TYPE', 'JSON_TYPEOF', 'JSON_VALID',
            'JSON_VALUE', 'JUSTIFY_DAYS', 'JUSTIFY_HOURS', 'JUSTIFY_INTERVAL', 'L2_DISTANCE', 'LAG', 'LASTVAL', 'LAST_DAY', 'LAST_VALUE',
            'LEAD', 'LENGTH', 'LOWER', 'LTRIM', 'MAKEDATE', 'MAKE_DATE',
            'MAKE_INTERVAL', 'MAKE_TIMESTAMP', 'MAX', 'MD5', 'MIN', 'MINUTE', 'MOD',
            'MONTH', 'MONTHNAME', 'NEXTVAL', 'NOW', 'NTH_VALUE', 'NTILE',
            'NULLIF', 'NVL', 'OCTET_LENGTH', 'PERCENT_RANK', 'POSITION', 'POW',
            'POWER', 'QUARTER', 'RANDOM', 'RANK', 'REGEXP_COUNT', 'REGEXP_INSTR',
            'REGEXP_LIKE', 'REGEXP_MATCH', 'REGEXP_MATCHES', 'REGEXP_REPLACE', 'REGEXP_SPLIT_TO_ARRAY', 'REGEXP_SPLIT_TO_TABLE',
            'REGEXP_SUBSTR', 'REPLACE', 'ROUND', 'ROW_NUMBER', 'RTRIM', 'SECOND',
            'SETVAL', 'SET_BIT', 'SET_BYTE', 'SHA1', 'SHA256', 'SHA512',
            'SIGN', 'SQRT', 'STARTS_WITH', 'STATEMENT_TIMESTAMP', 'STRPOS', 'STR_ID',
            'STR_TO_DATE', 'SUBSTR', 'SUBSTRING', 'SUM', 'SYSDATE', 'TO_BASE64', 'TO_BOOL',
            'TO_BYTES', 'TO_CHAR', 'TO_DATE', 'TO_DATETIME', 'TO_FLOAT32', 'TO_FLOAT64',
            'TO_ID', 'TO_INT64', 'TO_JSON', 'TO_JSONB', 'TO_STRING', 'TO_TIMESTAMP',
            'TRANSACTION_TIMESTAMP', 'TRIM', 'TRUNC', 'UNHEX', 'UNIX_TIMESTAMP', 'UNIX_TIMESTAMP_MS',
            'UPPER', 'VECTOR_DIMS', 'WEEK', 'WEEKDAY', 'YEAR',
        ],

        // Quantifiers of a comparison: `x > ALL (SELECT ...)`. See the header comment.
        quantifiers: ['ALL', 'ANY', 'SOME'],

        // The unit of INTERVAL '3' HOUR and INTERVAL 3 HOUR: each spelling IntervalMath.TryParseUnit
        // accepts. Monarch expands `@intervalUnit` in a rule's regex to this source. The `\b` after the
        // reference in each rule keeps a short spelling (m, s, d) from matching the start of a word.
        intervalUnit: 'millennium|millennia|millenniums|mils?|century|centuries|c|cent|decades?|decs?'
            + '|years?|y|yrs?|quarters?|months?|mons?|weeks?|w|days?|d|hours?|h|hrs?'
            + '|minutes?|m|mins?|seconds?|s|secs?|milliseconds?|ms|msecs?|microseconds?|us|usecs?',

        tokenizer: {
            root: [
                { include: '@comments' },
                { include: '@whitespace' },
                { include: '@numbers' },
                { include: '@dollarQuotes' },
                { include: '@strings' },
                { include: '@complexIdentifiers' },
                // One "table@index" pair, as SHOW RANGES / SHOW RANGE ... FROM INDEX accepts it. The
                // scanner matches it as a single TQUALIFIED_INDEX token, so the console must too: left
                // to the rule below, "users@users_pkey" splits into the identifier `users` plus a
                // `@users_pkey` painted as a bind parameter the user never wrote.
                //
                // The index half admits a leading '~' because the primary index's internal name is
                // "~pk", and that is the spelling SHOW INDEXES prints.
                //
                // An identifier character must precede the '@', so "= @p", "LIMIT @n" and
                // "VALUES(@a, @b)" stay parameters. This rule precedes '@scopes' as well, so a table
                // whose name is a keyword — "end@~pk" — is not split at the keyword either.
                [/[A-Za-z_]\w*@~?[A-Za-z_]\w*/, 'identifier'],

                { include: '@phrases' },
                { include: '@scopes' },

                // Query parameters: @name, as bound by CamusCommand.Parameters.
                [/@[A-Za-z_]\w*/, 'variable.parameter'],

                // ANY / SOME / ALL only as a quantifier, that is, with a '(' after it.
                [/(ALL|ANY|SOME)(?=\s*\()/i, 'operator'],

                // The postfix cast: price::int64, '{"a":1}'::string.
                [/::/, 'operator'],

                [/[;,.]/, 'delimiter'],
                [/[()\[\]]/, '@brackets'],
                // Table hints: FROM t@{FORCE_INDEX=idx}.
                [/[{}]/, 'delimiter.curly'],

                [/[A-Za-z_]\w*/, {
                    cases: {
                        '@operators': 'operator',
                        '@builtinFunctions': 'predefined',
                        '@builtinTypes': 'type',
                        '@keywords': 'keyword',
                        '@default': 'identifier',
                    },
                }],

                // The jsonb operators that start with '@' or '?'. The rest (->, ->>, #>, #>>, #-) are
                // made of characters the rule below already colors.
                [/@>|<@(?![A-Za-z0-9_])|\?[|&]?/, 'operator'],

                // Includes CamusDB's POSIX regex operators (~  ~*  !~  !~*), concatenation (||), the
                // bitwise operators (&  |  #  ~  <<  >>) and the power operator (^). '#' is XOR, never
                // a comment.
                [/[<>=!%&+\-*/|~#^]/, 'operator'],
            ],

            whitespace: [
                [/\s+/, 'white'],
            ],

            comments: [
                [/--+.*/, 'comment'],
                [/\/\*/, { token: 'comment.quote', next: '@comment' }],
            ],

            comment: [
                [/[^*/]+/, 'comment'],
                [/\*\//, { token: 'comment.quote', next: '@pop' }],
                [/./, 'comment'],
            ],

            // A digit run can carry single underscores between digits for grouping: 200_000,
            // 1_000.000_5, 1e1_0. Hex integers keep the plain form. See Digits in the lexer.
            numbers: [
                [/0[xX][0-9a-fA-F]*/, 'number'],
                [/((\d+(_\d+)*(\.(\d+(_\d+)*)?)?)|(\.\d+(_\d+)*))([eE][\-+]?\d+(_\d+)*)?/, 'number'],
            ],

            // Both quote styles are string literals in CamusDB — the lexer's String and StringSingle
            // both return TSTRING. This is NOT the ANSI/Monaco convention, where "..." is a quoted
            // identifier; getting it wrong renders every double-quoted literal as an identifier.
            strings: [
                [/'/, { token: 'string', next: '@stringSingle' }],
                [/"/, { token: 'string', next: '@stringDouble' }],
            ],

            stringSingle: [
                // \n, \377, \xFF, ￿, \UFFFFFFFF — EscChr and friends in the lexer.
                [/\\./, 'string.escape'],
                [/''/, 'string'],
                [/[^\\']+/, 'string'],
                [/'/, { token: 'string', next: '@pop' }],
            ],

            stringDouble: [
                [/\\./, 'string.escape'],
                [/""/, 'string'],
                [/[^\\"]+/, 'string'],
                [/"/, { token: 'string', next: '@pop' }],
            ],

            // $$ … $$ and $tag$ … $tag$: the C# body of an inline function or trigger. The state name
            // carries the tag ('dollarBody.' for $$, 'dollarBody.fn' for $fn$), and $S2 reads it back,
            // so only the same quote closes the body. Monaco looks for that closing quote before it
            // hands the line to the C# tokenizer, so a quote with another tag stays C# text, as in
            // the lexer. With no closing quote, the rest of the text is in the body.
            // The tag group always matches, so $1 is '' for $$. The quotes take the string color.
            dollarQuotes: [
                [/\$([A-Za-z_]\w*|)\$/, { token: 'string', next: '@dollarBody.$1', nextEmbedded: 'csharp' }],
            ],

            dollarBody: [
                [/\$$S2\$/, { token: 'string', next: '@pop', nextEmbedded: '@pop' }],
            ],

            // Backticks are the only identifier quoting CamusDB has (EscIdentifier). Tokenized as
            // `identifier.escaped` — its own type, not plain `identifier`, so the theme can color a
            // quoted name without repainting every bare column reference too.
            complexIdentifiers: [
                [/`/, { token: 'identifier.escaped', next: '@backtickedIdentifier' }],
            ],

            backtickedIdentifier: [
                [/[^`]+/, 'identifier.escaped'],
                [/`/, { token: 'identifier.escaped', next: '@pop' }],
            ],

            // Statement words the lexer leaves as plain identifiers and a parse action checks by text
            // (see the header comment). Each rule colors the whole phrase, so the word is colored
            // only where the grammar gives it a meaning. Monarch tokenizes one line at a time, so a
            // phrase split across lines stays uncolored.
            phrases: [
                // SHOW ENGINE STATS, SHOW SLOW QUERIES, SHOW USERS, SHOW RANGES FROM ... and the
                // others in show_stmt. No user name ever follows SHOW directly, so these are safe.
                [/SHOW\s+(?:ENGINE\s+STATS|STATEMENT\s+STATS|CLUSTER\s+(?:SETTINGS|QUERIES|CONNECTIONS)|SLOW\s+QUERIES|INDEX\s+USAGE|STATISTICS|VARIABLES|USERS|QUERIES|CONNECTIONS|EXTENSIONS|RANGES|RANGE)\b/i, 'keyword'],
                // SHOW RANGE FROM TABLE t FOR ROW (1). The '(' keeps SHOW GRANTS FOR row plain.
                [/FOR\s+ROW(?=\s*\()/i, 'keyword'],

                [/(?:SET|RESET)\s+CLUSTER\s+SETTING\b/i, 'keyword'],
                [/SET\s+TRANSACTION\s+(?:ISOLATION\s+LEVEL(?:\s+(?:SERIALIZABLE(?:\s+READ\s+(?:ONLY|WRITE))?|READ\s+COMMITTED))?|LOCKING(?:\s+(?:PESSIMISTIC|OPTIMISTIC))?|PRIORITY(?:\s+(?:BACKGROUND|LOW|NORMAL|HIGH|CRITICAL))?)\b/i, 'keyword'],

                [/FLUSH\s+(?:PRIVILEGES|SESSIONS)\b/i, 'keyword'],
                [/CANCEL\s+QUERY\b/i, 'keyword'],
                // CALL proc(...): the procedure name and its '(' are what make it a statement.
                [/CALL(?=\s+[A-Za-z_]\w*\s*\()/i, 'keyword'],
                [/EVICT\s+CACHE(?:\s+ALL)?\b/i, 'keyword'],
                [/(?:GRANT|REVOKE)\s+ALL(?:\s+PRIVILEGES)?\b/i, 'keyword'],

                // CREATE EXTENSION [IF NOT EXISTS] e VERSION '1.0', ALTER EXTENSION e UPDATE TO '1.1'.
                // The version is always a string literal, so the quote after VERSION is what pins it.
                [/(?:CREATE|ALTER|DROP)\s+EXTENSION\b/i, 'keyword'],
                [/VERSION(?=\s*['"])/i, 'keyword'],

                // Set operations: SELECT ... UNION ALL SELECT ...
                [/(?:UNION|INTERSECT|EXCEPT)\s+ALL\b/i, 'keyword'],

                // SELECT ... FOR [NO KEY] UPDATE [OF t] [NOWAIT | SKIP LOCKED]. NOWAIT ends the
                // statement, so it is colored only there. FOR SHARE parses only to be refused.
                [/FOR\s+(?:NO\s+KEY\s+)?UPDATE(?:\s+OF)?\b/i, 'keyword'],
                [/SKIP\s+LOCKED\b/i, 'keyword'],
                [/NOWAIT(?=\s*(?:;|$))/i, 'keyword'],

                // INSERT ... ON CONFLICT [(cols) | ON CONSTRAINT c] DO NOTHING | DO UPDATE SET ...,
                // and UPSERT INTO t ...
                [/ON\s+CONFLICT\b/i, 'keyword'],
                [/DO\s+(?:NOTHING|UPDATE)\b/i, 'keyword'],
                [/UPSERT(?=\s+INTO\b)/i, 'keyword'],

                // CREATE TRIGGER t ON tbl BEFORE INSERT, UPDATE EXTERNAL NAME 'Ns.Type', and the same with
                // LANGUAGE csharp AS $$ body $$. DROP TRIGGER t ON tbl.
                [/(?:CREATE|DROP)\s+TRIGGER\b/i, 'keyword'],
                [/(?:BEFORE|AFTER)(?=\s+(?:INSERT|UPDATE|DELETE)\b)/i, 'keyword'],
                [/EXTERNAL\s+NAME\b/i, 'keyword'],

                // Inline functions. ALTER FUNCTION f OWNER TO u takes the name, as ALTER VIEW does
                // below. RETURNS follows the ')' of the parameter list, so a column named returns
                // stays plain. The type after it is colored when it is BIGINT or JSONB, which are
                // plain identifiers too.
                [/(ALTER\s+FUNCTION)(\s+)([A-Za-z_]\w*)(\s+)(OWNER\s+TO)\b/i,
                    ['keyword', 'white', 'identifier', 'white', 'keyword']],
                [/(?:CREATE(?:\s+OR\s+REPLACE)?|ALTER|DROP)\s+FUNCTION\b/i, 'keyword'],
                [/SHOW\s+(?:CREATE\s+(?:FUNCTION|TRIGGER)|FUNCTIONS|TRIGGERS)\b/i, 'keyword'],
                [/(\))(\s*)(RETURNS)(\s+)(JSONB?|BIGINT)\b/i, ['delimiter.parenthesis', 'white', 'keyword', 'white', 'type']],
                [/(\))(\s*)(RETURNS)\b/i, ['delimiter.parenthesis', 'white', 'keyword']],
                [/(?:RETURNS\s+NULL|CALLED)\s+ON\s+NULL\s+INPUT\b/i, 'keyword'],
                // LANGUAGE csharp AS $$: the AS or the quote after the language name pins the phrase.
                [/(LANGUAGE)(\s+)([A-Za-z_]\w*)(?=\s+AS\b|\s*\$)/i, ['keyword', 'white', 'identifier']],

                // Views and materialized views.
                [/CREATE\s+OR\s+REPLACE\b/i, 'keyword'],
                [/WITH\s+(?:(?:LOCAL|CASCADED)\s+)?CHECK\s+OPTION\b/i, 'keyword'],
                [/WITH\s+(?:NO\s+)?DATA\b/i, 'keyword'],
                [/REFRESH\s+MATERIALIZED\s+VIEW\s+CONCURRENTLY\b/i, 'keyword'],
                // OWNER and CASCADE/RESTRICT follow a view name, so the rule takes the name too. A
                // backticked name does not match, and the words then stay uncolored.
                [/(ALTER\s+VIEW)(\s+)([A-Za-z_][\w.]*)(\s+)(OWNER\s+TO)\b/i,
                    ['keyword', 'white', 'identifier', 'white', 'keyword']],
                [/(DROP\s+(?:MATERIALIZED\s+)?VIEW(?:\s+IF\s+EXISTS)?)(\s+)([A-Za-z_][\w.]*(?:\s*,\s*[A-Za-z_][\w.]*)*)(\s+)(CASCADE|RESTRICT)\b/i,
                    ['keyword', 'white', 'identifier', 'white', 'keyword']],

                // TRUNCATE t RESTART IDENTITY, INSERT ... OVERRIDING SYSTEM VALUE.
                [/(?:RESTART|CONTINUE)\s+IDENTITY\b/i, 'keyword'],
                [/OVERRIDING\s+(?:SYSTEM|USER)\s+VALUE\b/i, 'keyword'],

                // Column definitions and ALTER TABLE storage forms.
                [/GENERATED\s+(?:ALWAYS|BY\s+DEFAULT)\s+AS\s+IDENTITY\b/i, 'keyword'],
                [/STORAGE\s+(?:EXTENDED|PLAIN|MAIN|EXTERNAL)\b/i, 'keyword'],
                [/REWRITE\s+STORAGE(?:\s+INLINE)?\b/i, 'keyword'],
                // `id serial`: the shorthand takes the place of a type, after a column name. The name
                // must not be a word the tokenizer colors, so `SELECT serial, x` stays plain.
                [/(?!@coloredWord)([A-Za-z_]\w*)(\s+)(SERIAL|BIGSERIAL)\b/i, ['identifier', 'white', 'type']],

                // Window clauses: OVER (PARTITION BY g ORDER BY d ROWS BETWEEN 2 PRECEDING AND CURRENT ROW).
                [/PARTITION\s+BY\b/i, 'keyword'],
                // ROWS and RANGE need a frame bound after them, so `WHERE range BETWEEN 1 AND 5` stays a
                // column compared with BETWEEN.
                [/(?:ROWS|RANGE)(?=\s+(?:BETWEEN\s+)?(?:UNBOUNDED\s|CURRENT\s+ROW\b|(?:\d+|@[A-Za-z_]\w*)\s+(?:PRECEDING|FOLLOWING)\b))/i, 'keyword'],
                [/UNBOUNDED\s+(?:PRECEDING|FOLLOWING)\b/i, 'keyword'],
                [/CURRENT\s+ROW\b/i, 'keyword'],
                [/(\d+)(\s+)(PRECEDING|FOLLOWING)\b/i, ['number', 'white', 'keyword']],
                [/(@[A-Za-z_]\w*)(\s+)(PRECEDING|FOLLOWING)\b/i, ['variable.parameter', 'white', 'keyword']],

                // INTERVAL literals. See the header comment. A form with a unit takes the whole phrase,
                // so the unit is colored only there. Without a unit, only the word is colored here, and
                // the string rules color the text after it.
                [/(INTERVAL)(\s*)('(?:[^'\\]|''|\\.)*')(\s+)(@intervalUnit)\b/i,
                    ['keyword', 'white', 'string', 'white', 'keyword']],
                [/(INTERVAL)(\s+)(\d+(?:\.\d+)?)(\s+)(@intervalUnit)\b/i,
                    ['keyword', 'white', 'number', 'white', 'keyword']],
                [/(INTERVAL)(\s*)(@[A-Za-z_]\w*)(\s+)(@intervalUnit)\b/i,
                    ['keyword', 'white', 'variable.parameter', 'white', 'keyword']],
                [/INTERVAL(?=\s*['@]|\s+\d)/i, 'keyword'],
                [/AGE(?=\s*\()/i, 'predefined'],

                // JSONB, JSON, BIGINT and INTERVAL, only where a type goes. See the header comment.
                // Only JSONB has a typed literal (JSONB '{}').
                [/(::)(\s*)(JSONB?|BIGINT|INTERVAL)\b/i, ['operator', 'white', 'type']],
                [/(AS)(\s+)(JSONB?|BIGINT|INTERVAL)(?=\s*\))/i, ['keyword', 'white', 'type']],
                [/(ARRAY)(\s*)(\()(\s*)(BIGINT)(?=\s*\))/i, ['type', 'white', 'delimiter.parenthesis', 'white', 'type']],
                [/JSONB?(?=\s*')/i, 'type'],
                [/(?!@coloredWord)([A-Za-z_]\w*)(\s+)(JSONB?)(?=\s*(?:[,)]|NOT\b|NULL\b|DEFAULT\b|COMMENT\b|STORAGE\b|$))/i,
                    ['identifier', 'white', 'type']],
                [/(?!@coloredWord)([A-Za-z_]\w*)(\s+)(BIGINT)(?=\s*(?:[,)]|NOT\b|NULL\b|DEFAULT\b|COMMENT\b|PRIMARY\b|UNIQUE\b|CHECK\b|REFERENCES\b|CONSTRAINT\b|GENERATED\b|$))/i,
                    ['identifier', 'white', 'type']],

                // GIN indexes: CREATE INDEX i ON t USING GIN (doc jsonb_path_ops), CREATE INVERTED
                // INDEX, and INVERTED INDEX i (doc) inside CREATE TABLE.
                [/USING\s+(?:GIN|BTREE)\b/i, 'keyword'],
                [/INVERTED(?=\s+INDEX\b)/i, 'keyword'],
                [/JSONB_(?:PATH_)?OPS\b/i, 'keyword'],

                // Foreign key clauses. NO ACTION is a lexer token; see '@scopes'.
                [/ON\s+(?:DELETE|UPDATE)\s+(?:CASCADE|RESTRICT)\b/i, 'keyword'],
                [/MATCH\s+(?:SIMPLE|FULL|PARTIAL)\b/i, 'keyword'],
                [/INITIALLY\s+(?:DEFERRED|IMMEDIATE)\b/i, 'keyword'],

                // Sequence options. Each valued option is colored only when its number follows.
                [/(?:INCREMENT(?:\s+BY)?|MINVALUE|MAXVALUE|CACHE)(?=\s+-?\d)/i, 'keyword'],
                [/RESTART(?:\s+WITH)?(?=\s+-?\d)/i, 'keyword'],
                [/NO\s+(?:MINVALUE|MAXVALUE|CYCLE)\b/i, 'keyword'],
            ],

            scopes: [
                // Multi-word forms the lexer matches as a single token.
                [/AS\s+OF\s+SYSTEM\s+TIME\b/i, 'keyword'],
                [/IF\s+NOT\s+EXISTS\b/i, 'keyword'],
                [/IF\s+EXISTS\b/i, 'keyword'],
                [/PRIMARY\s+KEY\b/i, 'keyword'],
                // DOUBLE PRECISION is one TTYPE_FLOAT64 token. The lexer also accepts a line break
                // between the two words, but Monarch tokenizes one line at a time.
                [/DOUBLE\s+PRECISION\b/i, 'type'],
                // ON DELETE NO ACTION: one TNOACTION token in the lexer. The lexer also accepts a line
                // break between the two words, but Monarch tokenizes one line at a time, so that form
                // leaves NO uncolored.
                [/NO\s+ACTION\b/i, 'keyword'],
                [/ORDER\s+BY\b/i, 'keyword'],
                [/GROUP\s+BY\b/i, 'keyword'],
                [/(BEGIN|CASE)\b/i, 'keyword.block'],
                [/END\b/i, 'keyword.block'],
                [/WHEN\b/i, 'keyword.choice'],
                [/THEN\b/i, 'keyword.choice'],
            ],
        },
    };

    // Any word the tokenizer colors as a keyword, operator or type. Monarch expands `@coloredWord` in a
    // rule's regex to this source, so the SERIAL rule in `phrases` reads the same lists as the cases.
    definition.coloredWord = new RegExp(
        `(?:${[...definition.keywords, ...definition.operators, ...definition.builtinTypes].join('|')})\\b`);

    const configuration = {
        comments: {
            lineComment: '--',
            blockComment: ['/*', '*/'],
        },
        brackets: [['(', ')'], ['[', ']']],
        autoClosingPairs: [
            { open: '(', close: ')' },
            { open: '[', close: ']' },
            { open: '"', close: '"' },
            { open: "'", close: "'" },
            { open: '`', close: '`' },
        ],
        surroundingPairs: [
            { open: '(', close: ')' },
            { open: '[', close: ']' },
            { open: '"', close: '"' },
            { open: "'", close: "'" },
            { open: '`', close: '`' },
        ],
    };

    /*
     * vs-dark and vs plus the console's own token colors. Rules are matched by longest dot-prefix,
     * and the tokens carry a `.sql` postfix, so these must name `<token>.sql` to beat monaco's
     * built-in `string.sql` (a hard red) rather than losing to it.
     */
    const themes = {
        'camus-dark': {
            base: 'vs-dark',
            inherit: true,
            rules: [
                // Both quote styles — CamusDB parses "..." and '...' alike.
                { token: 'string.sql', foreground: 'C792EA' },
                { token: 'string.escape.sql', foreground: 'E5B9F0' },

                // Backtick-quoted identifiers: `games`
                { token: 'identifier.escaped.sql', foreground: 'FA8072' },

                // Function names: pink in place of monaco's default magenta (FF00FF).
                { token: 'predefined.sql', foreground: 'FF79C6' },
            ],
            colors: {},
        },
        'camus-light': {
            base: 'vs',
            inherit: true,
            rules: [
                { token: 'string.sql', foreground: '8E3DAF' },
                { token: 'string.escape.sql', foreground: 'B565D0' },
                { token: 'identifier.escaped.sql', foreground: 'C0392B' },
                { token: 'predefined.sql', foreground: 'D6336C' },
            ],
            colors: {},
        },
    };

    /** The editor theme for the page theme, which App.razor and theme.js keep on <html>. */
    function themeId() {
        return document.documentElement.getAttribute('data-theme') === 'light' ? 'camus-light' : 'camus-dark';
    }

    /*
     * Schema-aware completion. QueryWorkspace hands over a DotNetObjectReference to
     * SchemaCompletionSource, which answers GetTables() and GetColumns(table) from SHOW TABLES and
     * SHOW COLUMNS and caches both on the server side. Without a source (not connected, or the
     * circuit is gone) the list holds keywords, types and functions only.
     */
    let completionSource = null;

    function setCompletionSource(source) {
        completionSource = source || null;
    }

    async function callSource(method, ...args) {
        if (!completionSource)
            return [];
        try {
            return (await completionSource.invokeMethodAsync(method, ...args)) || [];
        } catch {
            return [];
        }
    }

    // Upper-case words that a bare identifier must not use. A name in this set, or a name that is
    // not a plain identifier, is inserted with backticks.
    const reserved = new Set([...definition.keywords, ...definition.operators].map(w => w.toUpperCase()));

    function quoteName(name, afterBacktick) {
        if (afterBacktick)
            return name;
        return /^[A-Za-z_][A-Za-z0-9_]*$/.test(name) && !reserved.has(name.toUpperCase())
            ? name
            : '`' + name.replace(/`/g, '') + '`';
    }

    function unquote(name) {
        return name.length >= 2 && name[0] === '`' && name[name.length - 1] === '`' ? name.slice(1, -1) : name;
    }

    // $$ or $tag$ at lastIndex. Sticky, so it matches only where it starts.
    const dollarQuote = /\$(?:[A-Za-z_][A-Za-z0-9_]*)?\$/y;

    /*
     * The bounds of the statement around `offset`, cut at each ';' outside strings, backtick names,
     * comments and dollar-quoted bodies. These are the lexer's rules, as in SqlScript.cs; an E'...'
     * string is read as a plain string here, which can only make the bounds a little wrong for
     * completion.
     */
    function statementBounds(text, offset) {
        let start = 0;
        let i = 0;
        while (i < text.length) {
            const c = text[i];
            if (c === '$') {
                dollarQuote.lastIndex = i;
                const quote = dollarQuote.exec(text);
                if (quote) {
                    const end = text.indexOf(quote[0], i + quote[0].length);
                    i = end < 0 ? text.length : end + quote[0].length;
                    continue;
                }
            }
            if (c === '-' && text[i + 1] === '-') {
                const end = text.indexOf('\n', i);
                i = end < 0 ? text.length : end;
                continue;
            }
            if (c === '/' && text[i + 1] === '*') {
                const end = text.indexOf('*/', i + 2);
                i = end < 0 ? text.length : end + 2;
                continue;
            }
            if (c === "'" || c === '"' || c === '`') {
                i++;
                while (i < text.length) {
                    if (text[i] === c) {
                        if (c !== '`' && text[i + 1] === c) { i += 2; continue; }
                        break;
                    }
                    i++;
                }
                i++;
                continue;
            }
            if (c === ';') {
                if (i >= offset)
                    return { start, end: i };
                start = i + 1;
            }
            i++;
        }
        return { start, end: text.length };
    }

    // Words after which a name can follow but is not a table alias.
    const notAlias = new Set(['WHERE', 'JOIN', 'INNER', 'LEFT', 'RIGHT', 'OUTER', 'CROSS', 'ON', 'GROUP',
        'ORDER', 'LIMIT', 'OFFSET', 'SET', 'VALUES', 'HAVING', 'AS', 'USING', 'RETURNING', 'SELECT']);

    /** Maps each alias and each table name of the statement to its table. */
    function tableReferences(statement) {
        const refs = new Map();
        const pattern = /\b(?:FROM|JOIN|UPDATE|INTO)\s+(`[^`]+`|[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?:AS\s+)?(`[^`]+`|[A-Za-z_][A-Za-z0-9_]*))?/gi;
        let match;
        while ((match = pattern.exec(statement)) !== null) {
            const table = unquote(match[1]);
            if (reserved.has(table.toUpperCase()) && !match[1].startsWith('`'))
                continue;
            refs.set(table.toLowerCase(), table);
            if (match[2] && !notAlias.has(match[2].toUpperCase()))
                refs.set(unquote(match[2]).toLowerCase(), table);
        }
        return refs;
    }

    function columnItems(columns, table, range, afterBacktick, Kind, sortPrefix) {
        return columns.map(col => ({
            label: col.name,
            kind: Kind.Field,
            insertText: quoteName(col.name, afterBacktick),
            detail: `${table} · ${col.type || ''}${col.primaryKey ? ' · PK' : ''}`,
            sortText: sortPrefix + col.name,
            range,
        }));
    }

    async function provideItems(model, position, Kind, staticItems) {
        const word = model.getWordUntilPosition(position);
        const range = {
            startLineNumber: position.lineNumber,
            endLineNumber: position.lineNumber,
            startColumn: word.startColumn,
            endColumn: word.endColumn,
        };
        const keywords = staticItems.map(s => ({ ...s, range }));

        const text = model.getValue();
        const offset = model.getOffsetAt(position);
        const bounds = statementBounds(text, offset);
        const statement = text.slice(bounds.start, bounds.end);
        const before = text.slice(bounds.start, offset - word.word.length);
        const afterBacktick = before.endsWith('`');
        const refs = tableReferences(statement);

        // `alias.` or `table.`: only the columns of that table.
        const qualified = /(`[^`]+`|[A-Za-z_][A-Za-z0-9_]*)\.`?$/.exec(before);
        if (qualified) {
            const name = unquote(qualified[1]);
            const table = refs.get(name.toLowerCase()) || name;
            const columns = await callSource('GetColumns', table);
            return { suggestions: columnItems(columns, table, range, afterBacktick, Kind, '0') };
        }

        const tables = await callSource('GetTables');
        const tableItems = tables.map(t => ({
            label: t,
            kind: Kind.Class,
            insertText: quoteName(t, afterBacktick),
            detail: 'table',
            sortText: '1' + t,
            range,
        }));

        // After FROM, JOIN, INTO, UPDATE or TABLE the next word is a table name.
        if (/\b(?:FROM|JOIN|INTO|UPDATE|TABLE|DESCRIBE|TRUNCATE)\s+`?$/i.test(before))
            return { suggestions: [...tableItems.map(t => ({ ...t, sortText: '0' + t.label })), ...keywords] };

        const referenced = [...new Set(refs.values())];
        const columnLists = await Promise.all(referenced.map(t => callSource('GetColumns', t)));
        const columns = columnLists.flatMap((list, i) => columnItems(list, referenced[i], range, afterBacktick, Kind, '0'));

        return { suggestions: [...columns, ...tableItems, ...keywords] };
    }

    let registered = false;

    function register() {
        if (registered || !window.monaco || !window.monaco.languages)
            return registered;

        const languages = window.monaco.languages;
        if (languages.getLanguages().some(l => l.id === LANGUAGE_ID)) {
            registered = true;
            return true;
        }

        for (const [id, theme] of Object.entries(themes))
            window.monaco.editor.defineTheme(id, theme);

        languages.register({ id: LANGUAGE_ID, extensions: ['.sql'], aliases: ['CamusSQL', 'camussql'] });
        languages.setMonarchTokensProvider(LANGUAGE_ID, definition);
        languages.setLanguageConfiguration(LANGUAGE_ID, configuration);

        const Kind = languages.CompletionItemKind;
        const suggestions = [
            ...definition.keywords.map(w => ({ label: w, kind: Kind.Keyword, insertText: w })),
            // Not in the keyword list: a lone NO is an identifier. See the header comment.
            { label: 'NO ACTION', kind: Kind.Keyword, insertText: 'NO ACTION' },
            ...definition.operators.map(w => ({ label: w, kind: Kind.Operator, insertText: w })),
            ...definition.quantifiers.map(w => ({ label: w, kind: Kind.Operator, insertText: w })),
            ...definition.builtinTypes.map(w => ({ label: w, kind: Kind.TypeParameter, insertText: w })),
            // Not in the type list: json and jsonb are not reserved. See the header comment.
            { label: 'JSONB', kind: Kind.TypeParameter, insertText: 'JSONB' },
            { label: 'JSON', kind: Kind.TypeParameter, insertText: 'JSON' },
            { label: 'BIGINT', kind: Kind.TypeParameter, insertText: 'BIGINT' },
            { label: 'DOUBLE PRECISION', kind: Kind.TypeParameter, insertText: 'DOUBLE PRECISION' },
            // Not in the keyword list: INTERVAL is a keyword only before its literal. See the header
            // comment.
            { label: 'INTERVAL', kind: Kind.Keyword, insertText: 'INTERVAL' },
            // Not in the function list: AGE is colored only before a '('. See the header comment.
            {
                label: 'AGE',
                kind: Kind.Function,
                insertText: 'AGE($0)',
                insertTextRules: languages.CompletionItemInsertTextRule.InsertAsSnippet,
                detail: 'CamusDB function',
            },
            ...definition.builtinFunctions.map(w => ({
                label: w,
                kind: Kind.Function,
                insertText: `${w}($0)`,
                insertTextRules: languages.CompletionItemInsertTextRule.InsertAsSnippet,
                detail: 'CamusDB function',
            })),
        ].map(s => ({ ...s, sortText: '3' + s.label }));

        languages.registerCompletionItemProvider(LANGUAGE_ID, {
            triggerCharacters: ['.'],
            provideCompletionItems: (model, position) => provideItems(model, position, Kind, suggestions),
        });
        registered = true;
        return true;
    }

    /**
     * Retags an already-created editor. The script tags only *define* the monaco AMD modules — the
     * global appears later, asynchronously — so an editor could in principle be constructed against
     * a not-yet-registered language and fall back to plain text. QueryEditor calls this once the
     * editor reports ready, which is the one moment monaco is guaranteed to be loaded.
     */
    function applyToEditor(editorId) {
        if (!register())
            return false;

        const holder = (window.blazorMonaco && window.blazorMonaco.editors || [])
            .find(h => h.id === editorId);
        const model = holder && holder.editor && holder.editor.getModel();
        if (!model)
            return false;

        if (model.getLanguageId() !== LANGUAGE_ID)
            window.monaco.editor.setModelLanguage(model, LANGUAGE_ID);

        // The theme is global in monaco, and it is only defined once this script has run — assert it
        // here for the same reason the language is re-tagged.
        window.monaco.editor.setTheme(themeId());

        return true;
    }

    /** Called by theme.js after the page theme changes. The theme is global, so one call covers every editor. */
    function syncTheme() {
        if (register())
            window.monaco.editor.setTheme(themeId());
    }

    // The AMD loader is the supported way to wait for monaco; the poll only covers a page that
    // somehow has the global without the loader.
    if (!register()) {
        if (typeof window.require === 'function') {
            window.require(['vs/editor/editor.main'], register);
        } else {
            const timer = setInterval(() => {
                if (register())
                    clearInterval(timer);
            }, 50);
            setTimeout(() => clearInterval(timer), 30000);
        }
    }

    window.camusSql = {
        languageId: LANGUAGE_ID,
        ensureRegistered: register,
        applyToEditor: applyToEditor,
        setCompletionSource: setCompletionSource,
        syncTheme: syncTheme,
    };
})();
