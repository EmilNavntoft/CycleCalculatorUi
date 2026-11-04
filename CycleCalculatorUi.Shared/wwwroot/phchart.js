// Zoom and pan for the p-h diagram.
// Blazor renders the chart for a view (axis ranges); this module handles wheel and drag gestures. While a gesture
// runs, the already drawn curves are moved with a transform, which is exact because both axes are linear on screen
// (h, and log p). When the gesture pauses, the new view is sent to .NET, which redraws ticks and curves for it.

const DRAG_THRESHOLD = 4;
const SETTLE_MS = 150;      // send the view this long after the last wheel event
const DRAG_SEND_MS = 300;   // while dragging, refresh the real drawing this often

export function init(container, dotnet) {
    const state = { container, dotnet, target: null, rendered: null, seq: 0, busyUntil: 0, drag: null, timer: 0, suppressClick: false };

    state.onWheel = event => wheel(state, event);
    state.onPointerDown = event => pointerDown(state, event);
    state.onPointerMove = event => pointerMove(state, event);
    state.onPointerUp = event => pointerUp(state, event);
    state.onClick = event => {
        if (state.suppressClick) {
            state.suppressClick = false;
            event.stopPropagation();
            event.preventDefault();
        }
    };
    state.onDoubleClick = event => {
        const svg = chart(state);
        if (svg && inPlot(svg, toChart(svg, event))) {
            state.seq++;
            dotnet.invokeMethodAsync("ResetView", svg.dataset.fluid, state.seq);
        }
    };

    container.addEventListener("wheel", state.onWheel, { passive: false });
    container.addEventListener("pointerdown", state.onPointerDown);
    container.addEventListener("click", state.onClick, true);
    container.addEventListener("dblclick", state.onDoubleClick);
    window.addEventListener("pointermove", state.onPointerMove);
    window.addEventListener("pointerup", state.onPointerUp);
    window.addEventListener("pointercancel", state.onPointerUp);

    // Blazor re-renders change data-view; keep the preview transform in step with what is drawn.
    state.observer = new MutationObserver(() => sync(state));
    state.observer.observe(container, { subtree: true, childList: true, attributes: true, attributeFilter: ["data-view"] });
    sync(state);
    return state;
}

export function dispose(state) {
    if (!state) return;
    state.observer.disconnect();
    clearTimeout(state.timer);
    state.container.removeEventListener("wheel", state.onWheel);
    state.container.removeEventListener("pointerdown", state.onPointerDown);
    state.container.removeEventListener("click", state.onClick, true);
    state.container.removeEventListener("dblclick", state.onDoubleClick);
    window.removeEventListener("pointermove", state.onPointerMove);
    window.removeEventListener("pointerup", state.onPointerUp);
    window.removeEventListener("pointercancel", state.onPointerUp);
}

// ---------- geometry ----------

function chart(state) {
    return state.container.querySelector("svg.ph-chart");
}

/** The view as drawn: h in kJ/kg, pressure as natural log. */
function renderedView(svg) {
    const [h0, h1, p0, p1] = svg.dataset.view.split(" ").map(Number);
    return { fluid: svg.dataset.fluid, h0, h1, l0: Math.log(p0), l1: Math.log(p1) };
}

function plot(svg) {
    const [x, y, w, h] = svg.dataset.plot.split(" ").map(Number);
    return { x, y, w, h };
}

/** Client coordinates to SVG user units (handles the viewBox scaling). */
function toChart(svg, event) {
    const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(svg.getScreenCTM().inverse());
    return { x: point.x, y: point.y };
}

function inPlot(svg, point) {
    const p = plot(svg);
    return point.x >= p.x && point.x <= p.x + p.w && point.y >= p.y && point.y <= p.y + p.h;
}

// ---------- keeping the drawing and the target view in step ----------

function sync(state) {
    const svg = chart(state);
    if (!svg) return;
    const rendered = renderedView(svg);
    const renderedSeq = Number(svg.dataset.seq || 0);
    const idle = !state.drag && performance.now() > state.busyUntil;
    // Take over what .NET drew when no gesture is waiting for an answer, or when another fluid is shown.
    if (!state.target || state.target.fluid !== rendered.fluid || (idle && renderedSeq >= state.seq)) {
        state.target = { ...rendered };
    }
    state.rendered = rendered;
    applyPreview(state, svg);
}

/** Moves the drawn curves from the rendered view to the target view. */
function applyPreview(state, svg) {
    const layer = svg.querySelector("[data-zoom-layer]");
    if (!layer) return;
    const r = state.rendered, v = state.target, p = plot(svg);
    const ax = (r.h1 - r.h0) / (v.h1 - v.h0);
    const bx = p.x + (r.h0 - v.h0) / (v.h1 - v.h0) * p.w - ax * p.x;
    const ay = (r.l1 - r.l0) / (v.l1 - v.l0);
    const by = p.y + (v.l1 - r.l1) / (v.l1 - v.l0) * p.h - ay * p.y;
    const identity = Math.abs(ax - 1) < 1e-9 && Math.abs(ay - 1) < 1e-9 && Math.abs(bx) < 1e-6 && Math.abs(by) < 1e-6;
    if (identity) {
        layer.removeAttribute("transform");
    } else {
        layer.setAttribute("transform", `matrix(${ax} 0 0 ${ay} ${bx} ${by})`);
    }
    svg.classList.toggle("previewing", !identity);
}

function send(state) {
    clearTimeout(state.timer);
    const svg = chart(state);
    if (!svg || !state.target) return;
    const v = state.target;
    state.seq++;
    state.dotnet.invokeMethodAsync("SetView", v.fluid, v.h0, v.h1, Math.exp(v.l0), Math.exp(v.l1), state.seq);
}

// ---------- gestures ----------

function wheel(state, event) {
    const svg = chart(state);
    if (!svg || !state.target) return;
    const point = toChart(svg, event);
    const p = plot(svg);
    const overX = point.x >= p.x && point.x <= p.x + p.w;
    const overY = point.y >= p.y && point.y <= p.y + p.h;
    // Over the plot both axes zoom; below it only enthalpy, left of it only pressure.
    const zoomH = overX && point.y >= p.y;
    const zoomP = overY && point.x <= p.x + p.w;
    if (!zoomH && !zoomP) return;
    event.preventDefault();

    const v = state.target;
    const unitsPerPixel = 1 / svg.getScreenCTM().a;
    if (event.shiftKey) {
        // Shift+wheel pans, like the system canvas; trackpads report two-finger scrolling as deltaX/deltaY.
        const dx = (event.deltaX || event.deltaY) * unitsPerPixel, dy = (event.deltaX ? event.deltaY : 0) * unitsPerPixel;
        shift(v, dx / p.w * (v.h1 - v.h0), -dy / p.h * (v.l1 - v.l0));
    } else {
        const speed = event.deltaMode === 1 ? 0.05 : 0.0015;
        const f = Math.exp(event.deltaY * speed);
        if (zoomH && (overY || !zoomP)) {
            const h = v.h0 + (point.x - p.x) / p.w * (v.h1 - v.h0);
            v.h0 = h - (h - v.h0) * f;
            v.h1 = h + (v.h1 - h) * f;
        }
        if (zoomP && (overX || !zoomH)) {
            const l = v.l1 - (point.y - p.y) / p.h * (v.l1 - v.l0);
            v.l0 = l - (l - v.l0) * f;
            v.l1 = l + (v.l1 - l) * f;
        }
    }

    state.busyUntil = performance.now() + SETTLE_MS + 1000;
    applyPreview(state, svg);
    clearTimeout(state.timer);
    state.timer = setTimeout(() => {
        state.busyUntil = 0;
        send(state);
    }, SETTLE_MS);
}

function shift(v, dh, dl) {
    v.h0 += dh;
    v.h1 += dh;
    v.l0 += dl;
    v.l1 += dl;
}

function pointerDown(state, event) {
    const svg = chart(state);
    if (event.button !== 0 || !svg || !state.target || !inPlot(svg, toChart(svg, event))) return;
    state.drag = { x: event.clientX, y: event.clientY, start: { ...state.target }, moved: false, lastSend: 0 };
}

function pointerMove(state, event) {
    const drag = state.drag;
    const svg = chart(state);
    if (!drag || !svg) return;
    const dxPx = event.clientX - drag.x, dyPx = event.clientY - drag.y;
    if (!drag.moved && Math.hypot(dxPx, dyPx) < DRAG_THRESHOLD) return;
    drag.moved = true;
    svg.classList.add("panning");

    const unitsPerPixel = 1 / svg.getScreenCTM().a;
    const p = plot(svg), s = drag.start;
    const v = state.target = { ...s };
    shift(v, -dxPx * unitsPerPixel / p.w * (s.h1 - s.h0), dyPx * unitsPerPixel / p.h * (s.l1 - s.l0));
    applyPreview(state, svg);

    const now = performance.now();
    if (now - drag.lastSend > DRAG_SEND_MS) {
        drag.lastSend = now;
        send(state);
    }
}

function pointerUp(state) {
    const drag = state.drag;
    if (!drag) return;
    state.drag = null;
    chart(state)?.classList.remove("panning");
    if (drag.moved) {
        // The click that follows a drag must not select the component under the pointer.
        state.suppressClick = true;
        setTimeout(() => state.suppressClick = false, 0);
        send(state);
    }
}
