// Run: node --test frontend/tests/seriesMoveChoice.test.cjs
// Real AST-extracted callbacks/hooks/request serialization; synthetic React host and fetch.
// No HTTP, real files or DOM. Default hooks are synthetic; the last overlap
// case uses the real React Query MutationObserver lifecycle.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const ts = require('typescript');
const test = require('node:test');
const { QueryClient, MutationObserver } = require('@tanstack/react-query');

const root = path.resolve(__dirname, '../..');
const evidence = {
  compilerVersion: ts.version,
  sourceRoot: root,
  sourceCommit: '92a00a545655ee2eee311b8c24acfb9a7a8d25a4',
  sources: {},
  cases: [],
  requests: [],
  assertions: 0
};
const reads = new Map();

function source(relative) {
  if (reads.has(relative)) {
    return reads.get(relative);
  }

  const text = fs.readFileSync(path.join(root, relative), 'utf8');
  const sf = ts.createSourceFile(
    relative,
    text,
    ts.ScriptTarget.Latest,
    true,
    relative.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS
  );
  evidence.sources[relative] = {
    sha256: crypto.createHash('sha256').update(text).digest('hex'),
    extractions: []
  };
  reads.set(relative, sf);
  return sf;
}

function extract(relative, name, kind) {
  const sf = source(relative);
  let result = null;

  function visit(n) {
    if (
      (kind === 'function' &&
        ts.isFunctionDeclaration(n) &&
        n.name?.text === name) ||
      (kind === 'variable' &&
        ts.isVariableDeclaration(n) &&
        n.name.getText(sf) === name)
    ) {
      result = n;
    }

    ts.forEachChild(n, visit);
  }

  visit(sf);
  assert(result, `AST node ${name} exists in ${relative}`);
  const start = sf.getLineAndCharacterOfPosition(result.getStart(sf)).line + 1;
  evidence.sources[relative].extractions.push({
    name,
    kind,
    line: start,
    endLine: sf.getLineAndCharacterOfPosition(result.end).line + 1
  });
  return { node: result, sf };
}

const parts = [];

function addFunction(relative, name) {
  const { node, sf } = extract(relative, name, 'function');
  // Strip export modifier only; function body is original source.
  parts.push(node.getText(sf).replace(/^export(?:\s+default)?\s+/, ''));
}

function addVariable(relative, name) {
  const { node, sf } = extract(relative, name, 'variable');
  parts.push(`const ${node.getText(sf)};`);
}

addVariable('frontend/src/Utilities/Fetch/getQueryString.ts', 'getQueryString');
addVariable('frontend/src/Utilities/Fetch/getQueryPath.ts', 'getQueryPath');
addFunction('frontend/src/Utilities/Fetch/fetchJson.ts', 'fetchJson');
addFunction('frontend/src/Helpers/Hooks/useApiMutation.ts', 'useApiMutation');
addVariable('frontend/src/Series/useSeries.ts', 'useSaveSeries');
addVariable('frontend/src/Series/useSeries.ts', 'useSaveSeriesEditor');

function addComponent(relative, syntheticName, exports) {
  const { node, sf } = extract(relative, 'EditSeriesModalContent', 'function');
  const statements = [];

  for (const statement of node.body.statements) {
    if (ts.isReturnStatement(statement)) {
      break;
    } // Only JSX rendering omitted.

    statements.push(statement.getText(sf));
  }

  parts.push(
    `function ${syntheticName}(${node.parameters
      .map((p) => p.getText(sf))
      .join(', ')}) {\n${statements.join('\n')}\nreturn { ${exports.join(
      ', '
    )} };\n}`
  );
}

addComponent(
  'frontend/src/Series/Edit/EditSeriesModalContent.tsx',
  'SingleEdit',
  [
    'handleSavePress',
    'handleMoveSeriesPress',
    'handleCancelPress',
    'isPathChanging',
    'isConfirmMoveModalOpen'
  ]
);
addComponent(
  'frontend/src/Series/Index/Select/Edit/EditSeriesModalContent.tsx',
  'BulkEdit',
  [
    'onInputChange',
    'onSavePressWrapper',
    'onDoNotMoveSeriesPress',
    'onMoveSeriesPress',
    'isConfirmMoveModalOpen'
  ]
);
const footer = extract(
  'frontend/src/Series/Index/Select/SeriesIndexSelectFooter.tsx',
  'onSavePress',
  'variable'
);
parts.push(
  `function makeBulkSave(seriesIds, saveSeriesEditor) { const setIsSavingSeries = () => {}; const setIsEditModalOpen = () => {}; const ${footer.node.getText(
    footer.sf
  )}; return onSavePress; }`
);
parts.push(
  'globalThis.actual = { SingleEdit, BulkEdit, useSaveSeries, useSaveSeriesEditor, useApiMutation, makeBulkSave, getQueryString };'
);
const extracted = parts.join('\n\n');
const compiled = ts.transpileModule(extracted, {
  compilerOptions: {
    target: ts.ScriptTarget.ES2022,
    module: ts.ModuleKind.ES2022
  },
  reportDiagnostics: true
});
const errors = (compiled.diagnostics || []).filter(
  (d) => d.category === ts.DiagnosticCategory.Error
);
assert.equal(
  errors.length,
  0,
  `extracted source transpiles: ${errors
    .map((d) => ts.flattenDiagnosticMessageText(d.messageText, '\n'))
    .join('; ')}`
);

function harness({ deferred = false, realMutation = false } = {}) {
  let slots = [];
  let cursor = 0;
  let pendingChanges = {};
  const client = new QueryClient({
    defaultOptions: { mutations: { retry: false } }
  });
  const requests = [];
  const inflight = [];
  const completions = [];
  const fixture = {
    id: 101,
    title: 'Series',
    path: '/fixture/old/Series',
    rootFolderPath: '/fixture/old',
    monitored: true,
    monitorNewItems: 'all',
    seasonFolder: true,
    qualityProfileId: 1,
    seriesType: 'standard',
    tags: []
  };
  const ctx = vm.createContext({
    URLSearchParams,
    AbortController,
    setTimeout,
    clearTimeout,
    urlBase: '',
    apiRoot: '/api/v5',
    window: { Sonarr: { apiKey: 'SYNTHETIC' } },
    useState(initial) {
      const index = cursor++;

      if (!(index in slots)) {
        slots[index] = initial;
      }

      return [
        slots[index],
        (value) => {
          slots[index] = value;
        }
      ];
    },
    useCallback: (fn) => fn,
    useMemo: (fn) => fn(),
    useEffect: () => {},
    usePrevious: () => false,
    useSingleSeries: () => fixture,
    usePendingChangesStore: () => ({
      pendingChanges,
      setPendingChange: (name, value) => {
        pendingChanges[name] = value;
      }
    }),
    selectSettings: () => ({ settings: {} }),
    useQueryClient: () => client,
    useSelect: () => ({ selectedCount: 2 }),
    NO_CHANGE: 'noChange',
    anySignal: (values) => values[0],
    fetch: (requestPath, options) => {
      requests.push({
        path: requestPath,
        method: options.method,
        headers: options.headers,
        body: options.body === undefined ? undefined : JSON.parse(options.body),
        move: new URL(requestPath, 'https://fixture.invalid').searchParams.get(
          'moveFiles'
        )
      });
      const response = {
        ok: true,
        status: 200,
        json: async() => ({ ...fixture })
      };
      return deferred ?
        new Promise((resolve) => completions.push(() => resolve(response))) :
        Promise.resolve(response);
    },
    useMutation(options) {
      const observer = realMutation ?
        new MutationObserver(client, options) :
        null;
      return {
        mutate(data) {
          const promise = observer ?
            observer.mutate(data) :
            options.mutationFn(data);
          inflight.push(promise);
          return promise;
        },
        isPending: false,
        error: null
      };
    }
  });
  vm.runInContext(compiled.outputText, ctx, { timeout: 10000 });
  const props = { seriesId: 101, onModalClose: () => {}, onDeleteSeriesPress: () => {} };

  function render(fn = ctx.actual.SingleEdit, value = props) {
    cursor = 0;
    return fn(value);
  }

  return {
    actual: ctx.actual,
    requests,
    fixture,
    render,
    changes: (value) => {
      pendingChanges = value;
    },
    reset: () => {
      slots = [];
      pendingChanges = {};
    },
    settle: async() => {
      completions.reverse().forEach((fn) => fn());
      await Promise.all(inflight);
    },
    confirm: (move) => {
      let ui = render();
      ui.handleSavePress();
      ui = render();
      assert.equal(ui.isConfirmMoveModalOpen, true);

      if (move) {
        ui.handleMoveSeriesPress();
      } else {
        ui.handleSavePress();
      }
    }
  };
}

for (const move of [false, true]) {
  test(`single changed root: ${
    move ? 'Yes' : 'No'
  } carries selected move flag`, async() => {
    const h = harness();
    h.changes({ path: '/fixture/new/Series' });
    h.confirm(move);
    await h.settle();
    assert.equal(h.requests.length, 1);
    assert.equal(h.requests[0].move, String(move));
    assert.equal(h.requests[0].body.path, '/fixture/new/Series');
  });
}

test('unchanged path and metadata/monitoring edits default to no movement', async() => {
  const h = harness();
  h.changes({ monitored: false, qualityProfileId: 2 });
  h.render().handleSavePress();
  await h.settle();
  assert.equal(h.requests[0].move, 'false');
  assert.equal(h.requests[0].body.path, h.fixture.path);
  assert.equal(h.requests[0].body.monitored, false);
  assert.equal(h.requests[0].body.qualityProfileId, 2);
});
test('No -> Yes -> No on successive path edits never retains previous permission', async() => {
  const h = harness();

  for (const [index, move] of [false, true, false].entries()) {
    h.changes({ path: `/fixture/new${index}/Series` });
    h.confirm(move);
  }

  await h.settle();
  assert.deepEqual(
    h.requests.map((r) => r.move),
    ['false', 'true', 'false']
  );
  assert.deepEqual(
    h.requests.map((r) => r.body.path),
    ['/fixture/new0/Series', '/fixture/new1/Series', '/fixture/new2/Series']
  );
});
test('cancel sends no request; reopening after a previous Yes defaults back to No', async() => {
  const h = harness();
  h.changes({ path: '/fixture/first/Series' });
  h.confirm(true);
  await h.settle();
  h.changes({ path: '/fixture/second/Series' });
  h.render().handleSavePress();
  h.render().handleCancelPress();
  assert.equal(h.requests.length, 1);
  assert.equal(h.render().isConfirmMoveModalOpen, false);
  h.confirm(false);
  await h.settle();
  assert.deepEqual(
    h.requests.map((r) => r.move),
    ['true', 'false']
  );
});
test('same save callback pairs overlapping per-call bodies/flags, including absent flag', async() => {
  const h = harness({ deferred: true });
  const { saveSeries } = h.actual.useSaveSeries();
  saveSeries({ ...h.fixture, id: 11, path: '/fixture/A' }, true);
  saveSeries({ ...h.fixture, id: 22, path: '/fixture/B' }, false);
  saveSeries({ ...h.fixture, id: 33, path: '/fixture/C' });
  assert.deepEqual(
    h.requests.map((r) => [r.body.id, r.body.path, r.move]),
    [
      [11, '/fixture/A', 'true'],
      [22, '/fixture/B', 'false'],
      [33, '/fixture/C', 'false']
    ]
  );
  await h.settle();
});
test('series metadata, tags and season-monitoring request body stays unchanged', async() => {
  const h = harness();
  const payload = {
    ...h.fixture,
    tags: [8],
    alternateTitles: ['other'],
    images: [],
    seasons: [{ seasonNumber: 1, monitored: false, statistics: {} }]
  };
  h.actual.useSaveSeries().saveSeries(payload);
  await h.settle();
  assert.deepEqual(h.requests[0].body, payload);
});

for (const move of [false, true]) {
  test(`bulk changed root remains ${move}`, async() => {
    const h = harness();
    const hook = h.actual.useSaveSeriesEditor();
    const props = {
      onSavePress: h.actual.makeBulkSave([101, 102], hook.saveSeriesEditor),
      onModalClose: () => {}
    };
    let ui = h.render(h.actual.BulkEdit, props);
    ui.onInputChange({ name: 'rootFolderPath', value: '/fixture/new' });
    ui = h.render(h.actual.BulkEdit, props);
    ui.onSavePressWrapper();
    ui = h.render(h.actual.BulkEdit, props);

    if (move) {
      ui.onMoveSeriesPress();
    } else {
      ui.onDoNotMoveSeriesPress();
    }

    await h.settle();
    assert.equal(h.requests[0].body.moveFiles, move);
    assert.deepEqual(h.requests[0].body.seriesIds, [101, 102]);
  });
}

test('legacy shared mutation keeps static query/body, method and authentication headers', async() => {
  const h = harness();
  h.actual
    .useApiMutation({
      path: '/fixture',
      method: 'POST',
      queryParams: { force: false, marker: 'a b' },
      headers: { 'X-Fixture': 'present' }
    })
    .mutate({ id: 7 });
  await h.settle();
  const r = h.requests[0];
  const url = new URL(r.path, 'https://fixture.invalid');
  assert.equal(url.searchParams.get('force'), 'false');
  assert.equal(url.searchParams.get('marker'), 'a b');
  assert.deepEqual(r.body, { id: 7 });
  assert.equal(r.method, 'POST');
  assert.equal(r.headers['X-Fixture'], 'present');
  assert.equal(r.headers['X-Api-Key'], 'SYNTHETIC');
});
test('optional request builder pairs per-call body/query and overrides only matching static keys', async() => {
  const h = harness({ deferred: true });
  const hook = h.actual.useApiMutation({
    path: '/fixture',
    method: 'PUT',
    queryParams: { retained: 'yes', moveFiles: true },
    getRequest: ({ id, move }) => ({
      body: { id },
      queryParams: { moveFiles: move }
    })
  });
  hook.mutate({ id: 1, move: false });
  hook.mutate({ id: 2, move: true });
  assert.deepEqual(
    h.requests.map((r) => [r.body.id, r.move]),
    [
      [1, 'false'],
      [2, 'true']
    ]
  );
  assert.equal(
    new URL(h.requests[0].path, 'https://fixture.invalid').searchParams.get(
      'retained'
    ),
    'yes'
  );
  await h.settle();
});

for (const [name, value, expected] of [
  ['false', false, '?moveFiles=false'],
  ['true', true, '?moveFiles=true'],
  ['undefined', undefined, '?'],
  ['null', null, '?']
]) {
  test(`query serialization ${name}`, () => {
    const h = harness();
    assert.equal(h.actual.getQueryString({ moveFiles: value }), expected);
  });
}

test('legacy pre-serialized query and undefined body remain unchanged', async() => {
  const h = harness();
  h.actual
    .useApiMutation({
      path: '/queue/7?removeFromClient=false&blocklist=false',
      method: 'DELETE'
    })
    .mutate();
  await h.settle();
  assert.equal(
    h.requests[0].path,
    '/api/v5/queue/7?removeFromClient=false&blocklist=false'
  );
  assert.equal(h.requests[0].body, undefined);
});

test('real React Query overlapping mutations retain each immutable body/choice', async() => {
  const h = harness({ deferred: true, realMutation: true });
  const { saveSeries } = h.actual.useSaveSeries();
  saveSeries({ ...h.fixture, id: 1, path: '/fixture/realA' }, true);
  saveSeries({ ...h.fixture, id: 2, path: '/fixture/realB' }, false);
  saveSeries({ ...h.fixture, id: 3, path: '/fixture/realC' });
  await new Promise(setImmediate);
  assert.deepEqual(
    h.requests.map((r) => [r.body.id, r.body.path, r.move]),
    [
      [1, '/fixture/realA', 'true'],
      [2, '/fixture/realB', 'false'],
      [3, '/fixture/realC', 'false']
    ]
  );
  await h.settle();
});
