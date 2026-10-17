(function (root) {
  const Policy = Object.freeze({ refreshIntervalMilliseconds: 10000 });
  function createController({ resources, schedule = setTimeout, cancelScheduled = clearTimeout, onError = console.error }) {
    let context = null;
    let generation = 0;
    const operations = new Map();
    function cancel() {
      generation += 1;
      for (const operation of operations.values()) {
        operation.controller.abort();
        if (operation.timer !== null) cancelScheduled(operation.timer);
      }
      operations.clear();
    }
    function enabled(resource) {
      return context?.visible && context.authorized && resource.views.includes(context.view);
    }
    function refreshResource(resource) {
      if (!enabled(resource)) return Promise.resolve();
      const previous = operations.get(resource.name);
      if (previous?.pending) return previous.pending;
      if (previous?.timer !== null && previous?.timer !== undefined) cancelScheduled(previous.timer);
      const operation = { controller: new AbortController(), timer: null, pending: null };
      const currentGeneration = generation;
      const current = () => generation === currentGeneration && operations.get(resource.name) === operation && enabled(resource);
      operations.set(resource.name, operation);
      operation.pending = Promise.resolve().then(() => {
        if (current()) return resource.load(operation.controller.signal);
      }).then(result => {
        if (current()) resource.publish(result);
      }).catch(failure => {
        if (current() && failure?.name !== 'AbortError') onError(resource.name, failure);
      }).finally(() => {
        if (!current()) return;
        operation.pending = null;
        operation.timer = schedule(() => { operation.timer = null; void refreshResource(resource); }, Policy.refreshIntervalMilliseconds);
      });
      return operation.pending;
    }
    return {
      setContext(nextContext) {
        if (context && ['view', 'visible', 'authorized', 'identity'].every(key => context[key] === nextContext[key])) return;
        cancel();
        context = { ...nextContext };
        for (const resource of resources) void refreshResource(resource);
      },
      refresh(name) {
        return Promise.all(resources.filter(resource => !name || resource.name === name).map(refreshResource));
      },
      dispose() { cancel(); context = null; }
    };
  }
  const exported = { createController };
  if (typeof module !== 'undefined' && module.exports) module.exports = exported;
  else root.DashboardPolling = exported;
})(globalThis);
