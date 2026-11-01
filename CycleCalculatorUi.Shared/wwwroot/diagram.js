// Pointer interaction and connection routing for the cycle diagram.
// Blazor owns the document and renders nodes, ports and connection elements; this module only
// handles gestures (drag, connect, pan, zoom, rubber-band) and computes connection path geometry.
// Changes are reported to .NET once a gesture completes, so dragging stays smooth over a network.

const GRID = 16;
const STUB = 14;
const DRAG_THRESHOLD = 4;

export function init(svg, dotnet) {
    const viewport = svg.querySelector("[data-viewport]");
    const overlay = svg.querySelector("[data-overlay]");
    const state = { svg, viewport, overlay, dotnet, view: { x: 40, y: 40, k: 1 }, gesture: null, pending: null };

    applyView(state);

    state.onPointerDown = event => pointerDown(state, event);
    state.onPointerMove = event => pointerMove(state, event);
    state.onPointerUp = event => pointerUp(state, event);
    state.onWheel = event => wheel(state, event);
    state.onKeyDown = event => keyDown(state, event);
    state.onPalettePointerDown = event => palettePointerDown(state, event);

    svg.addEventListener("pointerdown", state.onPointerDown);
    svg.addEventListener("wheel", state.onWheel, { passive: false });
    window.addEventListener("pointermove", state.onPointerMove);
    window.addEventListener("pointerup", state.onPointerUp);
    window.addEventListener("pointercancel", state.onPointerUp);
    document.addEventListener("keydown", state.onKeyDown);
    document.addEventListener("pointerdown", state.onPalettePointerDown);
    svg.addEventListener("contextmenu", event => event.preventDefault());

    layout(state);
    return state;
}

export function dispose(state) {
    if (!state) return;
    state.svg.removeEventListener("pointerdown", state.onPointerDown);
    state.svg.removeEventListener("wheel", state.onWheel);
    window.removeEventListener("pointermove", state.onPointerMove);
    window.removeEventListener("pointerup", state.onPointerUp);
    window.removeEventListener("pointercancel", state.onPointerUp);
    document.removeEventListener("keydown", state.onKeyDown);
    document.removeEventListener("pointerdown", state.onPalettePointerDown);
    removeGhost(state);
}

// ---------- view (pan / zoom) ----------

function applyView(state) {
    const { x, y, k } = state.view;
    state.viewport.setAttribute("transform", `translate(${x} ${y}) scale(${k})`);
    state.svg.style.setProperty("--zoom", k);
}

export function zoom(state, factor) {
    const rect = state.svg.getBoundingClientRect();
    zoomAt(state, factor, rect.width / 2, rect.height / 2);
}

function zoomAt(state, factor, sx, sy) {
    const v = state.view;
    const k = Math.min(4, Math.max(0.2, v.k * factor));
    v.x = sx - (sx - v.x) * (k / v.k);
    v.y = sy - (sy - v.y) * (k / v.k);
    v.k = k;
    applyView(state);
}

export function fit(state) {
    const nodes = state.viewport.querySelectorAll("[data-node-id]");
    const rect = state.svg.getBoundingClientRect();
    if (nodes.length === 0 || rect.width === 0) {
        state.view = { x: 40, y: 40, k: 1 };
        applyView(state);
        return;
    }
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const node of nodes) {
        const box = boxInWorld(state, node);
        minX = Math.min(minX, box.x); minY = Math.min(minY, box.y);
        maxX = Math.max(maxX, box.x + box.width); maxY = Math.max(maxY, box.y + box.height);
    }
    const margin = 60;
    const k = Math.min(1.5, Math.max(0.2, Math.min(
        (rect.width - 2 * margin) / Math.max(1, maxX - minX),
        (rect.height - 2 * margin) / Math.max(1, maxY - minY))));
    state.view = {
        k,
        x: rect.width / 2 - k * (minX + maxX) / 2,
        y: rect.height / 2 - k * (minY + maxY) / 2
    };
    applyView(state);
}

/** World coordinates of the visible canvas centre, used when a palette item is clicked instead of dragged. */
export function viewCenter(state) {
    const rect = state.svg.getBoundingClientRect();
    return toWorld(state, rect.left + rect.width / 2, rect.top + rect.height / 2);
}

function wheel(state, event) {
    event.preventDefault();
    if (event.shiftKey) {
        // Shift+wheel pans; trackpads report two-finger scrolling as deltaX/deltaY.
        state.view.x -= event.deltaX || event.deltaY;
        state.view.y -= event.deltaX ? event.deltaY : 0;
        applyView(state);
        return;
    }
    const rect = state.svg.getBoundingClientRect();
    const speed = event.deltaMode === 1 ? 0.05 : 0.0015;
    zoomAt(state, Math.exp(-event.deltaY * speed), event.clientX - rect.left, event.clientY - rect.top);
}

function toWorld(state, clientX, clientY) {
    const rect = state.svg.getBoundingClientRect();
    const { x, y, k } = state.view;
    return { x: (clientX - rect.left - x) / k, y: (clientY - rect.top - y) / k };
}

function boxInWorld(state, element) {
    const box = element.getBBox();
    const m = state.viewport.getCTM().inverse().multiply(element.getCTM());
    const corners = [[box.x, box.y], [box.x + box.width, box.y], [box.x, box.y + box.height], [box.x + box.width, box.y + box.height]]
        .map(([px, py]) => new DOMPoint(px, py).matrixTransform(m));
    const xs = corners.map(p => p.x), ys = corners.map(p => p.y);
    return { x: Math.min(...xs), y: Math.min(...ys), width: Math.max(...xs) - Math.min(...xs), height: Math.max(...ys) - Math.min(...ys) };
}

// ---------- gestures ----------

function pointerDown(state, event) {
    if (state.pending && !event.target.closest("[data-port-key]")) {
        cancelPending(state);
    }

    // Middle or right button, or Alt+drag: pan.
    if (event.button === 1 || event.button === 2 || (event.button === 0 && event.altKey)) {
        event.preventDefault();
        state.gesture = { kind: "pan", startX: event.clientX, startY: event.clientY, view: { ...state.view } };
        return;
    }
    if (event.button !== 0) return;

    const port = event.target.closest("[data-port-key]");
    if (port) {
        event.preventDefault();
        if (state.pending) {
            if (state.pending.port === port) cancelPending(state);
            else finishConnection(state, state.pending.port, port);
            return;
        }
        state.gesture = { kind: "connect", port, startX: event.clientX, startY: event.clientY, moved: false };
        return;
    }

    const node = event.target.closest("[data-node-id]");
    if (node) {
        event.preventDefault();
        const id = node.dataset.nodeId;
        const additive = event.shiftKey || event.ctrlKey || event.metaKey;
        const alreadySelected = node.dataset.selected === "true";
        if (!alreadySelected || additive) {
            state.dotnet.invokeMethodAsync("SelectNode", id, additive);
        }
        const group = alreadySelected && !additive
            ? [...state.viewport.querySelectorAll("[data-node-id][data-selected='true']")]
            : [node];
        state.gesture = {
            kind: "move",
            startX: event.clientX,
            startY: event.clientY,
            moved: false,
            nodes: group.map(element => ({
                element,
                id: element.dataset.nodeId,
                x: parseFloat(element.dataset.x),
                y: parseFloat(element.dataset.y),
                transform: element.getAttribute("transform")
            }))
        };
        return;
    }

    const connection = event.target.closest("[data-connection-id]");
    if (connection) {
        event.preventDefault();
        state.dotnet.invokeMethodAsync("SelectConnection", connection.dataset.connectionId);
        return;
    }

    // Empty canvas: rubber-band selection, or clear the selection on a plain click.
    event.preventDefault();
    const start = toWorld(state, event.clientX, event.clientY);
    state.gesture = { kind: "select", start, startX: event.clientX, startY: event.clientY, moved: false, additive: event.shiftKey };
}

function pointerMove(state, event) {
    const g = state.gesture;
    if (state.pending && !g) {
        drawRubberLine(state, state.pending.port, toWorld(state, event.clientX, event.clientY));
        highlightTarget(state, event);
    }
    if (state.palette) {
        movePalette(state, event);
        return;
    }
    if (!g) return;

    const dx = event.clientX - g.startX, dy = event.clientY - g.startY;
    if (!g.moved && Math.hypot(dx, dy) > DRAG_THRESHOLD) g.moved = true;

    switch (g.kind) {
        case "pan":
            state.view.x = g.view.x + dx;
            state.view.y = g.view.y + dy;
            applyView(state);
            break;
        case "move": {
            if (!g.moved) return;
            const k = state.view.k;
            for (const n of g.nodes) {
                const x = snap(n.x + dx / k), y = snap(n.y + dy / k);
                n.newX = x; n.newY = y;
                n.element.setAttribute("transform", `translate(${x} ${y})`);
            }
            layout(state);
            break;
        }
        case "connect":
            if (!g.moved) return;
            drawRubberLine(state, g.port, toWorld(state, event.clientX, event.clientY));
            highlightTarget(state, event);
            break;
        case "select":
            if (!g.moved) return;
            drawSelectionBox(state, g.start, toWorld(state, event.clientX, event.clientY));
            break;
    }
}

function pointerUp(state, event) {
    if (state.palette) {
        dropPalette(state, event);
        return;
    }
    const g = state.gesture;
    state.gesture = null;
    if (!g) return;

    switch (g.kind) {
        case "move":
            if (g.moved) {
                const moves = g.nodes.filter(n => n.newX !== undefined).map(n => ({ id: n.id, x: n.newX, y: n.newY }));
                if (moves.length) state.dotnet.invokeMethodAsync("MoveNodes", moves);
            }
            break;
        case "connect": {
            if (!g.moved) {
                // Click on a port: start click-to-connect; the next port click completes it.
                state.pending = { port: g.port };
                g.port.classList.add("is-source");
                state.svg.classList.add("is-connecting");
                return;
            }
            const target = portUnder(event);
            clearOverlay(state);
            if (target && target !== g.port) finishConnection(state, g.port, target);
            break;
        }
        case "select": {
            clearOverlay(state);
            if (!g.moved) {
                state.dotnet.invokeMethodAsync("ClearSelection");
                break;
            }
            const end = toWorld(state, event.clientX, event.clientY);
            const box = normalise(g.start, end);
            const ids = [...state.viewport.querySelectorAll("[data-node-id]")]
                .filter(node => intersects(box, boxInWorld(state, node)))
                .map(node => node.dataset.nodeId);
            state.dotnet.invokeMethodAsync("SelectNodes", ids, g.additive);
            break;
        }
    }
}

function finishConnection(state, from, to) {
    cancelPending(state);
    state.dotnet.invokeMethodAsync("ConnectPorts",
        from.dataset.portNode, from.dataset.portKey, to.dataset.portNode, to.dataset.portKey);
}

function cancelPending(state) {
    if (state.pending) state.pending.port.classList.remove("is-source");
    state.pending = null;
    state.svg.classList.remove("is-connecting");
    clearOverlay(state);
}

function keyDown(state, event) {
    const target = event.target;
    if (target && (target.closest("input, textarea, select, [contenteditable='true']"))) return;
    if (event.key === "Escape") cancelPending(state);
    const handled = ["Delete", "Backspace", "Escape", "r", "R", "f", "F"].includes(event.key) ||
        ((event.ctrlKey || event.metaKey) && ["z", "Z", "y", "Y", "a", "A", "s", "S", "o", "O"].includes(event.key));
    if (!handled) return;
    event.preventDefault();
    state.dotnet.invokeMethodAsync("OnKey", event.key, event.ctrlKey || event.metaKey, event.shiftKey);
}

// ---------- palette drag and drop ----------

function palettePointerDown(state, event) {
    const item = event.target.closest("[data-palette-type]");
    if (!item || event.button !== 0) return;
    event.preventDefault();
    state.palette = { type: item.dataset.paletteType, item, startX: event.clientX, startY: event.clientY, moved: false };
}

function movePalette(state, event) {
    const p = state.palette;
    if (!p.moved && Math.hypot(event.clientX - p.startX, event.clientY - p.startY) > DRAG_THRESHOLD) {
        p.moved = true;
        p.ghost = p.item.cloneNode(true);
        p.ghost.classList.add("palette-ghost");
        document.body.appendChild(p.ghost);
    }
    if (p.ghost) {
        p.ghost.style.left = `${event.clientX}px`;
        p.ghost.style.top = `${event.clientY}px`;
        state.svg.classList.toggle("is-drop-target", isOverCanvas(state, event));
    }
}

function dropPalette(state, event) {
    const p = state.palette;
    removeGhost(state);
    state.palette = null;
    if (!p.moved) {
        const center = viewCenter(state);
        state.dotnet.invokeMethodAsync("DropComponent", p.type, snap(center.x - 48), snap(center.y - 32));
        return;
    }
    if (!isOverCanvas(state, event)) return;
    const world = toWorld(state, event.clientX, event.clientY);
    state.dotnet.invokeMethodAsync("DropComponent", p.type, snap(world.x - 48), snap(world.y - 32));
}

function removeGhost(state) {
    state.palette?.ghost?.remove();
    state.svg.classList.remove("is-drop-target");
}

function isOverCanvas(state, event) {
    const rect = state.svg.getBoundingClientRect();
    return event.clientX >= rect.left && event.clientX <= rect.right && event.clientY >= rect.top && event.clientY <= rect.bottom;
}

// ---------- overlay drawing ----------

function drawRubberLine(state, port, to) {
    const from = portPoint(state, port);
    clearOverlay(state);
    const line = svgElement("path", { class: "rubber-line", d: `M${from.x} ${from.y} L${to.x} ${to.y}` });
    state.overlay.appendChild(line);
}

function drawSelectionBox(state, a, b) {
    const box = normalise(a, b);
    clearOverlay(state);
    state.overlay.appendChild(svgElement("rect", { class: "selection-box", x: box.x, y: box.y, width: box.width, height: box.height }));
}

function highlightTarget(state, event) {
    state.viewport.querySelectorAll(".port.is-target").forEach(p => p.classList.remove("is-target"));
    const target = portUnder(event);
    if (target) target.classList.add("is-target");
}

function clearOverlay(state) {
    state.overlay.replaceChildren();
    state.viewport.querySelectorAll(".port.is-target").forEach(p => p.classList.remove("is-target"));
}

function portUnder(event) {
    const element = document.elementFromPoint(event.clientX, event.clientY);
    return element?.closest?.("[data-port-key]") ?? null;
}

// ---------- connection routing ----------

/** Recomputes the geometry of all connection paths from the current port positions. */
export function layout(state) {
    if (!state) return;
    const ports = new Map();
    for (const port of state.viewport.querySelectorAll("[data-port-key]")) {
        ports.set(`${port.dataset.portNode}|${port.dataset.portKey}`, port);
    }
    for (const connection of state.viewport.querySelectorAll("[data-connection-id]")) {
        const from = ports.get(connection.dataset.from);
        const to = ports.get(connection.dataset.to);
        if (!from || !to) continue;
        const d = route(portPoint(state, from), portDirection(state, from), portPoint(state, to), portDirection(state, to));
        for (const path of connection.querySelectorAll("path")) path.setAttribute("d", d);
    }
}

function portPoint(state, port) {
    const m = state.viewport.getCTM().inverse().multiply(port.getCTM());
    const cx = parseFloat(port.getAttribute("cx") ?? 0), cy = parseFloat(port.getAttribute("cy") ?? 0);
    return new DOMPoint(cx, cy).matrixTransform(m);
}

/** Outward direction of a port in world space, from its local direction and the node's rotation/flip. */
function portDirection(state, port) {
    const [dx, dy] = (port.dataset.dir ?? "0,0").split(",").map(Number);
    const m = state.viewport.getCTM().inverse().multiply(port.getCTM());
    const x = m.a * dx + m.c * dy, y = m.b * dx + m.d * dy;
    const length = Math.hypot(x, y) || 1;
    return { x: Math.round(x / length), y: Math.round(y / length) };
}

/** Orthogonal route with short stubs leaving each port along its outward direction. */
function route(p1, d1, p2, d2) {
    const a = { x: p1.x + d1.x * STUB, y: p1.y + d1.y * STUB };
    const b = { x: p2.x + d2.x * STUB, y: p2.y + d2.y * STUB };
    const points = [p1, a];
    const horizontal = d1.x !== 0;
    // One corner is enough when both ports face the corner, e.g. an outlet feeding a bottom inlet from below.
    if (d1.x !== 0 && d2.y !== 0 && (b.x - a.x) * d1.x >= 0 && (a.y - b.y) * d2.y >= 0) {
        points.push({ x: b.x, y: a.y });
    } else if (d1.y !== 0 && d2.x !== 0 && (b.y - a.y) * d1.y >= 0 && (a.x - b.x) * d2.x >= 0) {
        points.push({ x: a.x, y: b.y });
    } else if (d1.x !== 0 && d1.x === d2.x) {
        // Both ports face the same way: go around the outside instead of through the components.
        const x = d1.x < 0 ? Math.min(a.x, b.x) : Math.max(a.x, b.x);
        points.push({ x, y: a.y }, { x, y: b.y });
    } else if (d1.y !== 0 && d1.y === d2.y) {
        const y = d1.y < 0 ? Math.min(a.y, b.y) : Math.max(a.y, b.y);
        points.push({ x: a.x, y }, { x: b.x, y });
    } else if (horizontal) {
        const mx = (a.x + b.x) / 2;
        const forward = d1.x > 0 ? mx >= a.x : mx <= a.x;
        if (forward) {
            points.push({ x: mx, y: a.y }, { x: mx, y: b.y });
        } else {
            const my = (a.y + b.y) / 2;
            points.push({ x: a.x, y: my }, { x: b.x, y: my });
        }
    } else {
        const my = (a.y + b.y) / 2;
        const forward = d1.y > 0 ? my >= a.y : my <= a.y;
        if (forward) {
            points.push({ x: a.x, y: my }, { x: b.x, y: my });
        } else {
            const mx = (a.x + b.x) / 2;
            points.push({ x: mx, y: a.y }, { x: mx, y: b.y });
        }
    }
    points.push(b, p2);
    return simplify(points).map((p, i) => `${i ? "L" : "M"}${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(" ");
}

function simplify(points) {
    const result = [];
    for (const p of points) {
        const last = result[result.length - 1];
        if (last && Math.abs(last.x - p.x) < 0.01 && Math.abs(last.y - p.y) < 0.01) continue;
        if (result.length >= 2) {
            const prev = result[result.length - 2];
            const collinear = (Math.abs(prev.x - last.x) < 0.01 && Math.abs(last.x - p.x) < 0.01) ||
                (Math.abs(prev.y - last.y) < 0.01 && Math.abs(last.y - p.y) < 0.01);
            if (collinear) result.pop();
        }
        result.push(p);
    }
    return result;
}

// ---------- helpers ----------

function snap(value) {
    return Math.round(value / GRID) * GRID;
}

function normalise(a, b) {
    return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), width: Math.abs(a.x - b.x), height: Math.abs(a.y - b.y) };
}

function intersects(a, b) {
    return a.x < b.x + b.width && b.x < a.x + a.width && a.y < b.y + b.height && b.y < a.y + a.height;
}

function svgElement(name, attributes) {
    const element = document.createElementNS("http://www.w3.org/2000/svg", name);
    for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value);
    return element;
}
