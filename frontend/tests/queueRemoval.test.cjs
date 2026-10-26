const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const React = require('react');
const ts = require('typescript');
const { QueryClient, MutationObserver } = require('@tanstack/react-query');
const root = path.resolve(__dirname, '../src');

function harness(
  method = 'removeFromClient',
  blocklist = 'doNotBlocklist',
  actualMutation = false
) {
  const cache = new Map(),
    hosts = new Map(),
    requests = [],
    refreshes = [];
  let host,
    selected = [11, 22],
    cleared = 0,
    resolveRequest,
    rejectRequest;
  const effects = [];
  const client = new QueryClient({
    defaultOptions: { mutations: { retry: false } },
  });
  const mutationState = { isPending: false, error: null };
  const options = { removalMethod: method, blocklistMethod: blocklist };
  const records = [11, 22].map((id) => ({
    id,
    status: 'downloading',
    seriesId: 1,
    episodeIds: [id],
    downloadClientHasPostImportCategory: true,
  }));
  const hooks = {
    ...React,
    useCallback: (fn) => fn,
    useMemo: (fn) => fn(),
    useEffect(fn) {
      if (host.key === 'selection') effects.push(fn);
    },
    useRef: (value) => {
      const i = host.cursor++;
      return (host.state[i] ??= { current: value });
    },
    useState: (initial) => {
      const i = host.cursor++;
      if (!(i in host.state))
        host.state[i] = typeof initial === 'function' ? initial() : initial;
      const owner = host;
      return [
        host.state[i],
        (next) => {
          owner.state[i] =
            typeof next === 'function' ? next(owner.state[i]) : next;
          owner.updates = (owner.updates ?? 0) + 1;
        },
      ];
    },
  };
  const element = (name) => {
    const fn = () => null;
    fn.displayName = name;
    return fn;
  };
  const stubs = {
    react: hooks,
    zustand: {
      ...require('zustand'),
      useStore: (store, selector) => selector(store.getState()),
    },
    'zustand/react/shallow': { useShallow: (selector) => selector },
    '@tanstack/react-query': {
      keepPreviousData: (data) => data,
      useQueryClient: () => ({
        invalidateQueries: async (args) => {
          refreshes.push(args);
        },
      }),
      useMutation: (config) => {
        if (actualMutation) {
          const ref = hooks.useRef(null);
          if (!ref.current) {
            ref.current = new MutationObserver(client, config);
            ref.current.subscribe(() => {});
          }
          ref.current.setOptions(config);
          return {
            ...ref.current.getCurrentResult(),
            mutate: (data, callbacks) => {
              ref.current.mutate(data, callbacks).catch(() => {});
            },
          };
        }
        return {
          ...mutationState,
          reset: () => {
            mutationState.error = null;
          },
          mutate(data, callbacks = {}) {
            mutationState.isPending = true;
            mutationState.error = null;
            const pending = config.mutationFn(data);
            pending.then(
              async (value) => {
                await config.onSuccess?.(value, data);
                await config.onSettled?.(value, null, data);
                mutationState.isPending = false;
                callbacks.onSuccess?.(value, data);
              },
              async (error) => {
                mutationState.error = error;
                await config.onError?.(error, data);
                await config.onSettled?.(undefined, error, data);
                mutationState.isPending = false;
                callbacks.onError?.(error, data);
              }
            );
          },
        };
      },
    },
    'Utilities/Fetch/fetchJson': (request) => {
      requests.push(request);
      return new Promise((resolve, reject) => {
        resolveRequest = resolve;
        rejectRequest = reject;
      });
    },
    'Utilities/Fetch/getQueryPath': (value) => value,
    'Utilities/String/translate': (key) => key,
    'Helpers/Props': {
      kinds: { DANGER: 'danger' },
      sizes: {},
      inputTypes: {},
      icons: {},
      filterBuilderValueTypes: {},
      tooltipPositions: {},
      align: {},
    },
    'Helpers/Hooks/usePage': () => ({}),
    'Helpers/Hooks/usePagedApiQuery': () => ({ records, isLoading: false }),
    'Filters/useCustomFilters': { useCustomFiltersList: () => [] },
    'Utilities/Filter/findSelectedFilters': () => [],
    'Utilities/Object/selectUniqueIds': () => [],
    'Utilities/pagePopulator': {
      registerPagePopulator() {},
      unregisterPagePopulator() {},
    },
    'Commands/useCommands': {
      useExecuteCommand: () => () => {},
      useCommandExecuting: () => false,
    },
    'Episode/useEpisodes': () => ({ isFetched: true }),
    'Series/useSeries': { useSingleSeries: () => ({}) },
    'Episode/useEpisode': { useEpisodesWithIds: () => [] },
    'Settings/UI/useUiSettings': { useUiSettingsValues: () => ({}) },
    'App/Select/SelectContext': {
      SelectProvider: element('SelectProvider'),
      useSelect: () => ({
        useSelectedIds: () => selected,
        useIsSelected: () => true,
        toggleSelected() {},
        unselectAll: () => {
          cleared++;
          selected = [];
        },
      }),
    },
  };
  function load(id) {
    if (Object.hasOwn(stubs, id)) return stubs[id];
    let file = path.isAbsolute(id) ? id : path.join(root, id);
    if (id.endsWith('.css')) return {};
    if (!fs.existsSync(file))
      file += fs.existsSync(file + '.tsx') ? '.tsx' : '.ts';
    if (file.endsWith('queueOptionsStore.ts'))
      return {
        useQueueOptions: () => ({
          removalOptions: options,
          columns: [],
          pageSize: 20,
        }),
        useQueueOption: () => options,
        setQueueOption: (_, next) => Object.assign(options, next),
      };
    if (/Status[\\/]useQueueStatus\.tsx?$/.test(file))
      return () => ({ count: 2 });
    const real =
      /[\\/]Activity[\\/]Queue[\\/](useQueue\.ts|Queue\.tsx|QueueRow\.tsx|RemoveQueueItemModal\.tsx)$/.test(
        file
      ) ||
      file.endsWith('useApiMutation.ts') ||
      file.endsWith('getQueryString.ts') ||
      file.endsWith('useSelectStore.ts');
    if (!real) return element(id);
    if (cache.has(file)) return cache.get(file).exports;
    const module = { exports: {} };
    cache.set(file, module);
    const source = ts.transpileModule(fs.readFileSync(file, 'utf8'), {
      fileName: file,
      compilerOptions: {
        jsx: ts.JsxEmit.React,
        module: ts.ModuleKind.CommonJS,
        target: ts.ScriptTarget.ES2022,
        esModuleInterop: true,
      },
    }).outputText;
    vm.runInThisContext(
      `(function(require,module,exports,window){${source}\n})`,
      { filename: file }
    )(
      (name) =>
        load(
          name.startsWith('.') ? path.resolve(path.dirname(file), name) : name
        ),
      module,
      module.exports,
      { Sonarr: { apiKey: 'fixture-only' } }
    );
    return module.exports;
  }
  function render(key, fn) {
    host = hosts.get(key) ?? { state: [] };
    hosts.set(key, host);
    host.key = key;
    host.cursor = 0;
    return fn();
  }
  function all(node) {
    return React.isValidElement(node)
      ? [node, ...React.Children.toArray(node.props.children).flatMap(all)]
      : [];
  }
  return {
    load,
    render,
    all,
    requests,
    refreshes,
    options,
    mutationState,
    selection(items) {
      const value = render('selection', () =>
        load('App/Select/useSelectStore').default(items)
      );
      effects.splice(0).forEach((fn) => fn());
      return value;
    },
    hook(bulk = true) {
      return render('hook', () =>
        bulk
          ? load('Activity/Queue/useQueue').useRemoveQueueItems()
          : load('Activity/Queue/useQueue').useRemoveQueueItem(11)
      );
    },
    queue() {
      const outer = render('outer', () =>
        load('Activity/Queue/Queue').default()
      );
      return render('queue', () => outer.props.children.type());
    },
    row() {
      return render('row', () =>
        load('Activity/Queue/QueueRow').default({
          id: 11,
          episodeIds: [],
          columns: [{ name: 'actions', isVisible: true }],
          size: 1,
          sizeLeft: 1,
          seasonNumbers: [],
          languages: [],
          title: 'fixture',
          status: 'downloading',
          quality: {},
          onQueueRowModalOpenOrClose() {},
        })
      );
    },
    modal(extra = {}) {
      return render('modal', () =>
        load('Activity/Queue/RemoveQueueItemModal').default({
          isOpen: true,
          canChangeCategory: true,
          canIgnore: true,
          isPending: false,
          onRemovePress() {},
          onModalClose() {},
          ...extra,
        })
      );
    },
    async settle(status = 204) {
      if (status === 204) resolveRequest();
      else
        rejectRequest({
          statusCode: status,
          message: 'secret-path-do-not-display',
        });
      await new Promise(setImmediate);
    },
    get parentUpdates() {
      return hosts.get('queue')?.updates ?? 0;
    },
    get cleared() {
      return cleared;
    },
    get selected() {
      return selected;
    },
  };
}

for (const bulk of [false, true]) {
  for (const method of ['removeFromClient', 'changeCategory', 'ignore']) {
    for (const block of [
      'doNotBlocklist',
      'blocklistAndSearch',
      'blocklistOnly',
    ]) {
      test(`${
        bulk ? 'bulk' : 'single'
      } ${method}/${block} sends explicit API options`, async () => {
        const h = harness(method, block);
        const hook = h.hook(bulk);
        bulk
          ? hook.removeQueueItems({ ids: [11, 22] })
          : hook.removeQueueItem();
        const request = h.requests[0],
          url = new URL(request.path, 'http://fixture');
        assert.equal(request.method, 'DELETE');
        assert.equal(url.pathname, bulk ? '/queue/bulk' : '/queue/11');
        assert.equal(
          url.searchParams.get('removeFromClient'),
          String(method === 'removeFromClient')
        );
        assert.equal(url.searchParams.has('remove'), false);
        assert.equal(
          url.searchParams.get('changeCategory'),
          String(method === 'changeCategory')
        );
        assert.equal(
          url.searchParams.get('blocklist'),
          String(block !== 'doNotBlocklist')
        );
        assert.equal(
          url.searchParams.get('skipRedownload'),
          String(block === 'blocklistOnly')
        );
        if (bulk) assert.deepEqual(request.body, { ids: [11, 22] });
        await h.settle();
        assert.equal(h.refreshes.length, 1);
      });
    }
  }
  for (const status of [400, 404, 500])
    test(`${
      bulk ? 'bulk' : 'single'
    } ${status} exposes failure and refreshes uncertain results`, async () => {
      const h = harness();
      let hook = h.hook(bulk);
      bulk ? hook.removeQueueItems({ ids: [11, 22] }) : hook.removeQueueItem();
      await h.settle(status);
      hook = h.hook(bulk);
      assert.ok(hook.removeError);
      assert.equal(h.refreshes.length, 1);
      assert.equal(h.cleared, 0);
      assert.deepEqual(h.selected, [11, 22]);
    });
  test(`${
    bulk ? 'bulk' : 'single'
  } duplicate submit blocked before rerender; retry retains options`, async () => {
    const h = harness('ignore', 'blocklistOnly');
    const hook = h.hook(bulk);
    const submit = () =>
      bulk ? hook.removeQueueItems({ ids: [11, 22] }) : hook.removeQueueItem();
    submit();
    submit();
    assert.equal(h.requests.length, 1);
    await h.settle(500);
    const retry = h.hook(bulk);
    bulk ? retry.removeQueueItems({ ids: [22] }) : retry.removeQueueItem();
    assert.equal(h.requests.length, 2);
    assert.equal(h.requests[1].path, h.requests[0].path);
    if (bulk) assert.deepEqual(h.requests[1].body, { ids: [22] });
    await h.settle();
  });
}
for (const [method, block] of [
  [undefined, 'doNotBlocklist'],
  ['bad', 'doNotBlocklist'],
  ['ignore', undefined],
  ['ignore', 'bad'],
])
  test(`ambiguous persisted options ${method}/${block} do not submit`, () => {
    const h = harness();
    h.options.removalMethod = method;
    h.options.blocklistMethod = block;
    h.hook().removeQueueItems({ ids: [11] });
    assert.equal(h.requests.length, 0);
  });

test('bulk confirmation stays open on failure and closes only on 204; cancel sends no request', async () => {
  const h = harness();
  let tree = h.queue();
  let modal = () =>
    h
      .all(tree)
      .find(
        (n) => n.type === h.load('Activity/Queue/RemoveQueueItemModal').default
      );
  // Invoke the actual toolbar handler to open the dialog.
  h.all(tree)
    .find((n) => n.props.onPress && n.props.label === 'RemoveSelected')
    .props.onPress();
  tree = h.queue();
  assert.equal(modal().props.isOpen, true);
  modal().props.onRemovePress();
  tree = h.queue();
  assert.equal(modal().props.isOpen, true);
  await h.settle(500);
  tree = h.queue();
  assert.equal(modal().props.isOpen, true);
  assert.ok(modal().props.removeError);
  assert.equal(h.cleared, 0);
  modal().props.onRemovePress();
  await h.settle();
  tree = h.queue();
  assert.equal(modal().props.isOpen, false);
  const before = h.requests.length;
  modal().props.onModalClose();
  assert.equal(h.requests.length, before);
});

test('modal pending disables repeat/remove, changing options and closing', () => {
  const h = harness();
  let removed = 0,
    closed = 0;
  const nodes = h.all(
    h.modal({
      isRemoving: true,
      onRemovePress: () => removed++,
      onModalClose: () => closed++,
    })
  );
  const remove = nodes.find((n) => n.props.children === 'Remove');
  assert.equal(remove.props.isDisabled, true);
  remove.props.onPress();
  nodes[0].props.onModalClose();
  assert.equal(removed, 0);
  assert.equal(closed, 0);
  for (const input of nodes.filter(
    (n) =>
      n.props.name === 'blocklistMethod' || n.props.name === 'removalMethod'
  ))
    assert.equal(input.props.isDisabled, true);
});

test('modal renders sanitized failure and keeps confirmation options', () => {
  const h = harness('ignore', 'blocklistOnly');
  const tree = h.modal({
    removeError: { statusCode: 500, message: 'secret-path-do-not-display' },
  });
  const nodes = h.all(tree);
  assert.ok(nodes.some((n) => n.props.children === 'QueueRemoveError'));
  assert.equal(
    nodes.find((n) => n.props.name === 'removalMethod').props.value,
    'ignore'
  );
  assert.equal(
    nodes.find((n) => n.props.name === 'blocklistMethod').props.value,
    'blocklistOnly'
  );
  assert.equal(
    nodes.some((n) => n.props.children === 'secret-path-do-not-display'),
    false
  );
});

test('single row holds modal on failure, closes after 204, and cancel never submits', async () => {
  const h = harness();
  let tree = h.row();
  const modal = () =>
    h
      .all(tree)
      .find(
        (n) => n.type === h.load('Activity/Queue/RemoveQueueItemModal').default
      );
  const open = h
    .all(tree)
    .find((n) => n.props.onPress && n.props.isSpinning === false);
  // With only the action column, the last action is remove.
  const buttons = h.all(tree).filter((n) => n.props.onPress);
  buttons.at(-1).props.onPress();
  tree = h.row();
  assert.equal(modal().props.isOpen, true);
  modal().props.onRemovePress();
  tree = h.row();
  assert.equal(modal().props.isOpen, true);
  await h.settle(400);
  tree = h.row();
  assert.ok(modal().props.removeError);
  assert.equal(modal().props.isOpen, true);
  modal().props.onRemovePress();
  await h.settle();
  tree = h.row();
  assert.equal(modal().props.isOpen, false);
  const before = h.requests.length;
  modal().props.onModalClose();
  assert.equal(h.requests.length, before);
});

test('closing a row modal schedules parent rendering of refreshed queue rows', () => {
  const h = harness();
  const tree = h.queue();
  const content = tree.props.children.find(
    (n) => n && h.all(n).some((x) => x.props.onQueueRowModalOpenOrClose)
  );
  const row = h.all(content).find((n) => n.props.onQueueRowModalOpenOrClose);
  row.props.onQueueRowModalOpenOrClose(true);
  const before = h.parentUpdates;
  row.props.onQueueRowModalOpenOrClose(false);
  assert.ok(
    h.parentUpdates > before,
    'parent must render after unblocking, not only mutate a ref'
  );
});

test('unavailable stored ignore cannot submit but can be explicitly changed to client removal', () => {
  const h = harness('ignore');
  let nodes = h.all(h.modal({ canIgnore: false, canChangeCategory: false }));
  assert.equal(
    nodes.find((n) => n.props.children === 'Remove').props.isDisabled,
    true
  );
  const input = nodes.find((n) => n.props.name === 'removalMethod');
  assert.equal(!!input.props.isDisabled, false);
  input.props.onChange({ name: 'removalMethod', value: 'removeFromClient' });
  nodes = h.all(h.modal({ canIgnore: false, canChangeCategory: false }));
  assert.equal(
    nodes.find((n) => n.props.children === 'Remove').props.isDisabled,
    false
  );
});

test('real selection store retains selected remaining rows after partial refresh and reconciles full success', () => {
  const h = harness();
  let state = h.selection([{ id: 11 }, { id: 22 }, { id: 33 }]);
  state.toggleSelected({ id: 11, isSelected: true, shiftKey: false });
  state.toggleSelected({ id: 22, isSelected: true, shiftKey: false });
  assert.deepEqual(state.getSelectedIds(), [11, 22]);
  state = h.selection([{ id: 22 }, { id: 33 }]);
  assert.deepEqual(state.getSelectedIds(), [22]);
  state = h.selection([{ id: 33 }]);
  assert.deepEqual(state.getSelectedIds(), []);
});

for (const status of [204, 400, 404, 500])
  test(`real React Query mutation lifecycle ${status}: refresh, error, success callback and retry`, async () => {
    const h = harness('changeCategory', 'blocklistOnly', true);
    let successes = 0;
    let hook = h.hook();
    hook.removeQueueItems({ ids: [11, 22] }, () => successes++);
    hook.removeQueueItems({ ids: [11, 22] }, () => successes++);
    await new Promise(setImmediate);
    assert.equal(h.requests.length, 1);
    await h.settle(status);
    await new Promise((resolve) => setTimeout(resolve, 10));
    hook = h.hook();
    assert.equal(h.refreshes.length, 1);
    assert.equal(hook.isRemoving, false);
    assert.equal(successes, status === 204 ? 1 : 0);
    assert.equal(!!hook.removeError, status !== 204);
    if (status !== 204) {
      hook.removeQueueItems({ ids: [22] }, () => successes++);
      await new Promise(setImmediate);
      assert.deepEqual(h.requests[1].body, { ids: [22] });
      await h.settle();
      await new Promise((resolve) => setTimeout(resolve, 10));
      assert.equal(successes, 1);
    }
  });
