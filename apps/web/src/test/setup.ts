import "@testing-library/jest-dom/vitest";

// jsdom doesn't implement ResizeObserver or layout — TanStack Virtual needs
// both. The stubs below give the virtualizer a nonzero viewport so it emits
// virtual items during tests instead of collapsing to zero rows.

class NoopResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
}
Object.defineProperty(globalThis, "ResizeObserver", {
    writable: true, configurable: true, value: NoopResizeObserver,
});

const withHeight = 800;
const withWidth = 1200;
Object.defineProperty(HTMLElement.prototype, "clientHeight", {
    configurable: true, get() { return withHeight; },
});
Object.defineProperty(HTMLElement.prototype, "clientWidth", {
    configurable: true, get() { return withWidth; },
});
Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
    configurable: true, get() { return withHeight; },
});
Object.defineProperty(HTMLElement.prototype, "offsetWidth", {
    configurable: true, get() { return withWidth; },
});
HTMLElement.prototype.getBoundingClientRect = function () {
    return {
        x: 0, y: 0, top: 0, left: 0, right: withWidth, bottom: withHeight,
        width: withWidth, height: withHeight, toJSON() { return {}; },
    } as DOMRect;
};
