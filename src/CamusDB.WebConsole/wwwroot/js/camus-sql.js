/*
 * A Monaco language for CamusDB SQL.
 *
 * Monaco's built-in "sql" mode is T-SQL flavored: it misses SHOW, TABLES, DATABASES, STRING, OID,
 * INT64, BOOL, ILIKE, BRANCH, GRANTS and a couple of dozen more, and it highlights plenty that
 * CamusDB does not accept. The word lists below are derived from the server's own lexer
 * (CamusDB.Core/SQLParser/SQLParser.Language.analyzer.lex) and scalar-function registry, so the
 * editor colors exactly what the engine parses — no more and no less.
 *
 * Deliberately absent: UNION, ALL, LEFT/RIGHT/OUTER/CROSS JOIN. CamusDB has no tokens for them, and
 * `all` is explicitly kept unreserved so it stays usable as a column name.
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
            'CREATE', 'DATABASE', 'DATABASES', 'DEFAULT', 'DELETE', 'DESC',
            'DESCRIBE', 'DISTINCT', 'DROP', 'ELSE', 'END', 'EVICT',
            'EXPLAIN', 'FALSE', 'FOR', 'FORCE', 'FROM', 'GRANT',
            'GRANTS', 'GROUP', 'HAVING', 'IDENTIFIED', 'IF', 'INCLUDE',
            'INDEX', 'INDEXES', 'INNER', 'INSERT', 'INTO', 'JOIN',
            'KEY', 'LIMIT', 'MATERIALIZED', 'NULL', 'OFFSET', 'ON',
            'ORDER', 'ORPHAN', 'PRIMARY', 'PRIVILEGES', 'REFRESH', 'RELINK',
            'RENAME', 'RESET', 'REVOKE',
            'ROLLBACK', 'SELECT', 'SET', 'SHOW', 'START', 'TABLE',
            'TABLES', 'THEN', 'TO', 'TRANSACTION', 'TRUE', 'UNIQUE',
            'UPDATE', 'USER', 'VALUES', 'VIEW', 'VIEWS', 'WHEN',
            'WHERE', 'WITH',
        ],

        operators: [
            'AND', 'BETWEEN', 'EXISTS', 'ILIKE', 'IN', 'IS',
            'LIKE', 'NOT', 'OR',
        ],

        builtinTypes: [
            'ARRAY', 'BLOB', 'BOOL', 'BOOLEAN', 'BYTES', 'CHAR',
            'DATE', 'DATETIME', 'DOUBLE', 'FLOAT', 'FLOAT32', 'FLOAT64',
            'GUID', 'INT', 'INT64', 'INTEGER', 'OBJECT_ID', 'OID',
            'REAL', 'SMALLINT', 'STRING', 'TEXT', 'TIMESTAMP', 'UUID',
            'VARCHAR',
        ],

        // Registered scalar functions plus the five aggregates. Aliases count: the registry resolves
        // CEILING/POWER/NVL/NOW to the same descriptors as CEIL/POW/IFNULL/CURRENT_TIMESTAMP, so
        // leaving them out colored a call the engine accepts as a plain identifier.
        builtinFunctions: [
            'ABS', 'AVG', 'CEIL', 'CEILING', 'COALESCE', 'CONCAT',
            'CONTAINS', 'COUNT', 'COUNT_DISTINCT', 'CURRENT_DATABASE', 'CURRENT_DATE', 'CURRENT_ROLE',
            'CURRENT_TIMESTAMP', 'CURRENT_USER', 'DATE_ADD', 'DATE_DIFF', 'DATE_PART', 'DATE_TRUNC',
            'ENDS_WITH', 'FLOOR', 'FROM_UNIXTIME', 'GEN_ID', 'GEN_UUID_V4', 'GEN_UUID_V7',
            'IFNULL', 'IS_SUPERUSER', 'JSON_ARRAY_LENGTH', 'JSON_CONTAINS', 'JSON_EXTRACT', 'JSON_TYPE',
            'JSON_VALID', 'JSON_VALUE', 'LENGTH', 'LOWER', 'LTRIM', 'MAX',
            'MIN', 'MOD', 'NOW', 'NVL', 'POW', 'POWER',
            'RANDOM', 'REGEXP_COUNT', 'REGEXP_INSTR', 'REGEXP_LIKE', 'REGEXP_MATCH', 'REGEXP_MATCHES',
            'REGEXP_REPLACE', 'REGEXP_SPLIT_TO_ARRAY', 'REGEXP_SPLIT_TO_TABLE', 'REGEXP_SUBSTR', 'REPLACE', 'ROUND',
            'RTRIM', 'SIGN', 'SQRT', 'STARTS_WITH', 'SUBSTRING', 'SUM',
            'TO_BOOL', 'TO_BYTES', 'TO_DATE', 'TO_DATETIME', 'TO_FLOAT32', 'TO_FLOAT64',
            'TO_ID', 'TO_INT64', 'TO_STRING', 'TRIM', 'UNIX_TIMESTAMP', 'UPPER',
        ],

        tokenizer: {
            root: [
                { include: '@comments' },
                { include: '@whitespace' },
                { include: '@numbers' },
                { include: '@strings' },
                { include: '@complexIdentifiers' },
                { include: '@scopes' },

                // Query parameters: @name, as bound by CamusCommand.Parameters.
                [/@[A-Za-z_]\w*/, 'variable.parameter'],

                [/[;,.]/, 'delimiter'],
                [/[()]/, '@brackets'],

                [/[A-Za-z_]\w*/, {
                    cases: {
                        '@operators': 'operator',
                        '@builtinFunctions': 'predefined',
                        '@builtinTypes': 'type',
                        '@keywords': 'keyword',
                        '@default': 'identifier',
                    },
                }],

                // Includes CamusDB's POSIX regex operators: ~  ~*  !~  !~*
                [/[<>=!%&+\-*/|~^]/, 'operator'],
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

            numbers: [
                [/0[xX][0-9a-fA-F]*/, 'number'],
                [/((\d+(\.\d*)?)|(\.\d+))([eE][\-+]?\d+)?/, 'number'],
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

            scopes: [
                // Multi-word forms the lexer matches as a single token.
                [/AS\s+OF\s+SYSTEM\s+TIME\b/i, 'keyword'],
                [/IF\s+NOT\s+EXISTS\b/i, 'keyword'],
                [/IF\s+EXISTS\b/i, 'keyword'],
                [/PRIMARY\s+KEY\b/i, 'keyword'],
                [/ORDER\s+BY\b/i, 'keyword'],
                [/GROUP\s+BY\b/i, 'keyword'],
                [/(BEGIN|CASE)\b/i, 'keyword.block'],
                [/END\b/i, 'keyword.block'],
                [/WHEN\b/i, 'keyword.choice'],
                [/THEN\b/i, 'keyword.choice'],
            ],
        },
    };

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

    const THEME_ID = 'camus-dark';

    /*
     * vs-dark plus the console's own token colors. Rules are matched by longest dot-prefix, and the
     * tokens carry a `.sql` postfix, so these must name `<token>.sql` to beat monaco's built-in
     * `string.sql` (a hard red) rather than losing to it.
     */
    const theme = {
        base: 'vs-dark',
        inherit: true,
        rules: [
            // Both quote styles — CamusDB parses "..." and '...' alike.
            { token: 'string.sql', foreground: 'C792EA' },
            { token: 'string.escape.sql', foreground: 'E5B9F0' },

            // Backtick-quoted identifiers: `games`
            { token: 'identifier.escaped.sql', foreground: 'FA8072' },
        ],
        colors: {},
    };

    let registered = false;

    function register() {
        if (registered || !window.monaco || !window.monaco.languages)
            return registered;

        const languages = window.monaco.languages;
        if (languages.getLanguages().some(l => l.id === LANGUAGE_ID)) {
            registered = true;
            return true;
        }

        window.monaco.editor.defineTheme(THEME_ID, theme);

        languages.register({ id: LANGUAGE_ID, extensions: ['.sql'], aliases: ['CamusSQL', 'camussql'] });
        languages.setMonarchTokensProvider(LANGUAGE_ID, definition);
        languages.setLanguageConfiguration(LANGUAGE_ID, configuration);

        const Kind = languages.CompletionItemKind;
        const suggestions = [
            ...definition.keywords.map(w => ({ label: w, kind: Kind.Keyword, insertText: w })),
            ...definition.operators.map(w => ({ label: w, kind: Kind.Operator, insertText: w })),
            ...definition.builtinTypes.map(w => ({ label: w, kind: Kind.TypeParameter, insertText: w })),
            ...definition.builtinFunctions.map(w => ({
                label: w,
                kind: Kind.Function,
                insertText: `${w}($0)`,
                insertTextRules: languages.CompletionItemInsertTextRule.InsertAsSnippet,
                detail: 'CamusDB function',
            })),
        ];

        languages.registerCompletionItemProvider(LANGUAGE_ID, {
            provideCompletionItems: (model, position) => {
                const word = model.getWordUntilPosition(position);
                const range = {
                    startLineNumber: position.lineNumber,
                    endLineNumber: position.lineNumber,
                    startColumn: word.startColumn,
                    endColumn: word.endColumn,
                };
                return { suggestions: suggestions.map(s => ({ ...s, range })) };
            },
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
        window.monaco.editor.setTheme(THEME_ID);

        return true;
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

    window.camusSql = { languageId: LANGUAGE_ID, ensureRegistered: register, applyToEditor: applyToEditor };
})();
