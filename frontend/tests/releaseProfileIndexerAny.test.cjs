// Run with: node --test frontend/tests/releaseProfileIndexerAny.test.cjs
// No browser/test framework dependency: execute the real TSX render and change
// handlers with a minimal hook host; stub layout, translations and network I/O.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const React = require('react');
const { renderToStaticMarkup } = require('react-dom/server');
const ts = require('typescript');

const root = path.resolve(__dirname, '..', 'src');
const select = 'Components/Form/Select/';
const indexers = [
  { id: 1, name: 'Alpha' },
  { id: 2, name: 'Beta' },
];

function harness(data = indexers, mobile = false) {
  const cache = new Map();
  const state = [];
  let cursor = 0;
  let changed;
  let request;
  const element = ({ children }) => React.createElement('div', null, children);
  const hooks = {
    ...React,
    useMemo: (fn) => fn(),
    useCallback: (fn) => fn,
    useEffect: () => {},
    useRef: (current) => ({ current }),
    useState: (initial) => {
      const slot = cursor++;
      if (!(slot in state)) state[slot] = initial;
      return [
        state[slot],
        (next) => {
          state[slot] = typeof next === 'function' ? next(state[slot]) : next;
        },
      ];
    },
  };
  const stubs = {
    react: hooks,
    '@floating-ui/react': {
      autoUpdate() {},
      flip() {},
      size() {},
      FloatingPortal: element,
      useClick: () => ({}),
      useDismiss: () => ({}),
      useInteractions: () => ({
        getReferenceProps: () => ({}),
        getFloatingProps: () => ({}),
      }),
      useFloating: () => ({ refs: {}, context: {}, floatingStyles: {} }),
    },
    'Settings/Indexers/useIndexers': {
      useSortedIndexers: () => ({ data, isFetching: false }),
    },
    'Utilities/String/translate': (key) => key,
    'Utilities/browser': { isMobile: () => mobile },
    'Helpers/Props': { icons: {}, kinds: {}, sizes: {} },
    'Components/Label': ({ children }) =>
      React.createElement('span', null, children),
    '@tanstack/react-query': {
      useQueryClient: () => ({}),
      useMutation: ({ mutationFn }) => ({ mutate: (args) => mutationFn(args) }),
    },
    'Utilities/Fetch/fetchJson': (args) => {
      request = args;
      return Promise.resolve(args.body);
    },
    'Utilities/Fetch/getQueryPath': (value) => value,
    'Utilities/Fetch/getQueryString': () => '',
  };
  function load(file) {
    if (Object.hasOwn(stubs, file)) return stubs[file];
    if (file.endsWith('.css')) return new Proxy({}, { get: (_, key) => key });
    let full = path.isAbsolute(file) ? file : path.join(root, file);
    if (!fs.existsSync(full))
      full += fs.existsSync(`${full}.tsx`) ? '.tsx' : '.ts';
    if (!fs.existsSync(full)) return require(file);
    // Keep layout, API stores and controls outside the behavior under test stubbed.
    if (
      !full.includes(`${path.sep}Select${path.sep}`) &&
      !full.endsWith('useProviderSettings.ts') &&
      !full.endsWith('keyCodes.ts')
    )
      return element;
    if (cache.has(full)) return cache.get(full).exports;
    const module = { exports: {} };
    cache.set(full, module);
    const source = ts.transpileModule(fs.readFileSync(full, 'utf8'), {
      compilerOptions: {
        jsx: ts.JsxEmit.React,
        module: ts.ModuleKind.CommonJS,
        target: ts.ScriptTarget.ES2020,
        esModuleInterop: true,
      },
    }).outputText;
    const localRequire = (id) =>
      load(id.startsWith('.') ? path.resolve(path.dirname(full), id) : id);
    vm.runInThisContext(
      `(function(require,module,exports,window){${source}\n})`,
      { filename: full }
    )(localRequire, module, module.exports, {
      Sonarr: { apiKey: 'test-only' },
    });
    return module.exports;
  }
  function all(node) {
    if (!React.isValidElement(node)) return [];
    return [node, ...React.Children.toArray(node.props.children).flatMap(all)];
  }
  const Indexer = load(`${select}IndexerSelectInput`).default;
  const Enhanced = load(`${select}EnhancedSelectInput`).default;
  const Selected = load(`${select}HintedSelectInputSelectedValue`).default;
  const Option = load(`${select}HintedSelectInputOption`).default;
  return {
    render(value, extra = {}) {
      changed = undefined;
      cursor = 0;
      const input = Indexer({
        name: 'indexerIds',
        value,
        onChange: (change) => {
          changed = change;
        },
        ...extra,
      });
      state[1] = true; // Open the existing option list without a browser layout engine.
      const nodes = all(Enhanced(input.props));
      const selected = nodes.find((node) => node.type === Selected);
      const options = nodes.filter((node) => node.type === Option);
      return {
        html: renderToStaticMarkup(selected),
        options,
        input: input.props,
        select(id) {
          options.find((option) => option.props.id === id).props.onSelect(id);
          return changed;
        },
        keyDown(keyCode) {
          nodes
            .find((node) => node.props.onKeyDown)
            .props.onKeyDown({
              keyCode,
              preventDefault() {},
              stopPropagation() {},
            });
          return changed;
        },
      };
    },
    async save(profile) {
      const { save } = load(
        'Settings/useProviderSettings'
      ).useSaveProviderSettings(profile.id, '/releaseprofile');
      save(profile);
      return request;
    },
    load,
  };
}

test('new and persisted empty indexer selections display (Any) without mutation', () => {
  const h = harness();
  for (const value of [[], JSON.parse('{"indexerIds":[]}').indexerIds]) {
    const input = h.render(value);
    assert.match(input.html, /\(Any\)/);
    assert.strictEqual(input.input.value, value);
    assert.deepEqual(value, []);
    assert.deepEqual(
      input.options.map((option) => option.props.id),
      [1, 2]
    );
  }
});

test('selecting one and multiple indexers preserves names and checkbox selections', () => {
  const h = harness();
  const one = h.render([]).select(1);
  assert.deepEqual(Array.from(one.value), [1]);
  let view = h.render(one.value);
  assert.match(view.html, /Alpha/);
  assert.doesNotMatch(view.html, /\(Any\)/);
  const multiple = view.select(2);
  assert.deepEqual(Array.from(multiple.value), [1, 2]);
  view = h.render(multiple.value);
  assert.match(view.html, /Alpha/);
  assert.match(view.html, /Beta/);
  assert.deepEqual(
    view.options.map((option) => option.props.isSelected),
    [true, true]
  );
});

test('clearing the last selection restores (Any) and saves an empty array', async () => {
  const h = harness();
  const cleared = h.render([1]).select(1);
  assert.equal(cleared.name, 'indexerIds');
  assert.deepEqual(Array.from(cleared.value), []);
  assert.match(h.render(cleared.value).html, /\(Any\)/);
  const request = await h.save({
    id: 12,
    name: 'Existing profile',
    indexerIds: cleared.value,
  });
  assert.equal(request.method, 'PUT');
  assert.equal(request.path, '/releaseprofile/12');
  assert.deepEqual(JSON.parse(JSON.stringify(request.body)).indexerIds, []);
  assert.doesNotMatch(JSON.stringify(request.body), /Any/);
});

test('new indexers remain options without changing the empty selection', () => {
  const value = [];
  const view = harness([...indexers, { id: 3, name: 'New indexer' }]).render(
    value
  );
  assert.match(view.html, /\(Any\)/);
  assert.deepEqual(
    view.options.map((option) => option.props.id),
    [1, 2, 3]
  );
  assert.deepEqual(value, []);
});

test('empty option lists and mobile selections still display (Any)', () => {
  assert.match(harness([]).render([]).html, /\(Any\)/);
  const view = harness(indexers, true).render([]);
  assert.match(view.html, /\(Any\)/);
  assert.deepEqual(
    view.options.map((option) => option.props.id),
    [1, 2]
  );
});

test('keyboard selection and removing one of several indexers retain existing behavior', () => {
  const h = harness();
  const first = h.render([]);
  first.keyDown(40);
  const selected = h.render([]).keyDown(13);
  assert.deepEqual(Array.from(selected.value), [1]);
  const remaining = h.render([1, 2]).select(1);
  assert.deepEqual(Array.from(remaining.value), [2]);
  assert.match(h.render(remaining.value).html, /Beta/);
});

test('other selectors keep the original blank empty display by default', () => {
  const h = harness();
  const Selected = h.load(`${select}HintedSelectInputSelectedValue`).default;
  const html = renderToStaticMarkup(
    React.createElement(Selected, {
      selectedValue: [],
      values: [],
      isMultiSelect: true,
    })
  );
  assert.doesNotMatch(html, /Any/);
});
