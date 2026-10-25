const attachments = new WeakMap();

export function attach(root, canvas, dotnet) {
    let activePointer = null;

    const onPointerDown = event => {
        if (event.button > 0) {
            return;
        }

        const port = event.target.closest("[data-port-component][data-port-id]");
        if (port) {
            return;
        }

        const paletteItem = event.target.closest("[data-palette-kind]");
        const placedItem = event.target.closest("[data-instance-id]");

        if (!paletteItem && !placedItem) {
            return;
        }

        event.preventDefault();
        root.setPointerCapture(event.pointerId);

        if (paletteItem) {
            activePointer = {
                pointerId: event.pointerId,
                kind: paletteItem.dataset.paletteKind,
                startX: event.clientX,
                startY: event.clientY,
                moved: false,
                ghost: createGhost(paletteItem)
            };
            moveGhost(activePointer.ghost, event.clientX, event.clientY);
            return;
        }

        const itemRect = placedItem.getBoundingClientRect();
        activePointer = {
            pointerId: event.pointerId,
            id: placedItem.dataset.instanceId,
            item: placedItem,
            offsetX: event.clientX - itemRect.left,
            offsetY: event.clientY - itemRect.top,
            startX: event.clientX,
            startY: event.clientY,
            moved: false
        };
    };

    const onPointerMove = event => {
        if (!activePointer || activePointer.pointerId !== event.pointerId) {
            return;
        }

        if (Math.hypot(event.clientX - activePointer.startX, event.clientY - activePointer.startY) > 4) {
            activePointer.moved = true;
        }

        if (activePointer.ghost) {
            moveGhost(activePointer.ghost, event.clientX, event.clientY);
        } else if (activePointer.moved) {
            movePlacedItem(activePointer, event.clientX, event.clientY);
        }
    };

    const onPointerUp = async event => {
        if (!activePointer || activePointer.pointerId !== event.pointerId) {
            return;
        }

        const pointer = activePointer;
        activePointer = null;

        if (pointer.ghost) {
            pointer.ghost.remove();
            const canvasRect = canvas.getBoundingClientRect();
            const droppedOnCanvas = event.clientX >= canvasRect.left &&
                event.clientX <= canvasRect.right &&
                event.clientY >= canvasRect.top &&
                event.clientY <= canvasRect.bottom;

            if (droppedOnCanvas || !pointer.moved) {
                const workspace = canvas.querySelector(".canvas-workspace");
                const workspaceRect = workspace.getBoundingClientRect();
                const x = droppedOnCanvas
                    ? event.clientX - workspaceRect.left - 60
                    : canvas.scrollLeft + canvas.clientWidth / 2 - 60;
                const y = droppedOnCanvas
                    ? event.clientY - workspaceRect.top - 38
                    : canvas.scrollTop + canvas.clientHeight / 2 - 38;
                await dotnet.invokeMethodAsync("AddComponent", pointer.kind, x, y);
            }
            return;
        }

        if (pointer.moved) {
            const position = getCanvasPosition(canvas, pointer.item, event.clientX, event.clientY, pointer);
            await dotnet.invokeMethodAsync("MoveComponent", pointer.id, position.x, position.y);
        } else {
            await dotnet.invokeMethodAsync("SelectComponent", pointer.id);
        }
    };

    const onPointerCancel = event => {
        if (activePointer?.pointerId === event.pointerId) {
            activePointer.ghost?.remove();
            activePointer = null;
        }
    };

    root.addEventListener("pointerdown", onPointerDown);
    root.addEventListener("pointermove", onPointerMove);
    root.addEventListener("pointerup", onPointerUp);
    root.addEventListener("pointercancel", onPointerCancel);
    attachments.set(root, () => {
        activePointer?.ghost?.remove();
        root.removeEventListener("pointerdown", onPointerDown);
        root.removeEventListener("pointermove", onPointerMove);
        root.removeEventListener("pointerup", onPointerUp);
        root.removeEventListener("pointercancel", onPointerCancel);
    });
}

export function detach(root) {
    attachments.get(root)?.();
    attachments.delete(root);
}

function createGhost(source) {
    const ghost = source.cloneNode(true);
    ghost.classList.add("drag-ghost");
    ghost.removeAttribute("data-palette-kind");
    ghost.removeAttribute("aria-label");
    document.body.append(ghost);
    return ghost;
}

function moveGhost(ghost, x, y) {
    ghost.style.left = `${x}px`;
    ghost.style.top = `${y}px`;
}

function movePlacedItem(pointer, clientX, clientY) {
    const item = pointer.item;
    const workspace = item.closest(".canvas-workspace");
    const workspaceRect = workspace.getBoundingClientRect();
    const x = clientX - workspaceRect.left - pointer.offsetX;
    const y = clientY - workspaceRect.top - pointer.offsetY;
    item.style.left = `${Math.max(12, Math.min(x, workspace.clientWidth - item.offsetWidth - 12))}px`;
    item.style.top = `${Math.max(12, Math.min(y, workspace.clientHeight - item.offsetHeight - 12))}px`;
}

function getCanvasPosition(canvas, item, clientX, clientY, pointer) {
    const workspaceRect = canvas.querySelector(".canvas-workspace").getBoundingClientRect();
    const workspace = canvas.querySelector(".canvas-workspace");
    const x = clientX - workspaceRect.left - pointer.offsetX;
    const y = clientY - workspaceRect.top - pointer.offsetY;
    return {
        x: Math.max(12, Math.min(x, workspace.clientWidth - item.offsetWidth - 12)),
        y: Math.max(12, Math.min(y, workspace.clientHeight - item.offsetHeight - 12))
    };
}
