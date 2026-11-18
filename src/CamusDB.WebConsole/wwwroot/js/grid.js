/**
 * The cell selection and the copy of the results grid (ResultsGrid.razor).
 *
 * The selection lives on the server, in the grid component. The table is virtualized, so the page
 * holds only the rows on the screen, and a copy can hold rows that the page never rendered. This
 * script turns mouse and key input into calls to the grid, and writes the text that the grid returns
 * to the clipboard.
 *
 * The listeners are on the document, so they work for each grid that a tab renders later. A grid
 * registers itself with its data-grid-id after its first render.
 */
window.camusGrid = (function () {
  /** data-grid-id → DotNetObjectReference of the grid. */
  const grids = new Map();

  /** The grid and the last cell under the pointer while the left button is down on a cell. */
  let drag = null;

  function gridOf(element) {
    const wrap = element && element.closest ? element.closest(".results-grid-wrap[data-grid-id]") : null;
    if (!wrap) return null;
    const ref = grids.get(wrap.dataset.gridId);
    return ref ? { wrap, ref } : null;
  }

  function cellOf(element) {
    const td = element && element.closest ? element.closest("td[data-c]") : null;
    const tr = td ? td.closest("tr[data-r]") : null;
    if (!tr) return null;
    return { row: Number(tr.dataset.r), column: Number(td.dataset.c) };
  }

  function call(ref, method, ...args) {
    // A call that fails after the circuit closes has nothing to tell.
    ref.invokeMethodAsync(method, ...args).catch(() => {});
  }

  function errorText(error) {
    const text = error && error.message ? error.message : String(error || "");
    return text || null;
  }

  /** The old way: a hidden text area and execCommand. Pages that are not https or localhost have no navigator.clipboard. */
  function copyWithTextArea(text, wrap) {
    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.top = "0";
    area.style.left = "0";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();

    let copied = false;
    try {
      copied = document.execCommand("copy");
    } finally {
      document.body.removeChild(area);
      if (wrap) wrap.focus({ preventScroll: true });
    }

    if (!copied) throw new Error("the browser refused access to the clipboard.");
  }

  /**
   * Writes the text of a promise to the clipboard. Safari allows a clipboard write only inside the
   * click or the key press, and the text comes from the server after it. A ClipboardItem takes a
   * promise of the text, so the write starts inside the event and ends when the text comes.
   */
  async function writeClipboard(textPromise, wrap) {
    const clipboard = window.isSecureContext ? navigator.clipboard : null;

    if (clipboard && clipboard.write && typeof ClipboardItem === "function") {
      const blob = textPromise.then((text) => new Blob([text], { type: "text/plain" }));
      blob.catch(() => {});
      try {
        await clipboard.write([new ClipboardItem({ "text/plain": blob })]);
        return;
      } catch {
        // A browser without promises in ClipboardItem. The code below tries again.
      }
    }

    // This await gives the error of the server, when the grid could not make the text.
    const text = await textPromise;

    if (clipboard && clipboard.writeText) {
      try {
        await clipboard.writeText(text);
        return;
      } catch {
        // The code below tries the old way.
      }
    }

    copyWithTextArea(text, wrap);
  }

  function copy(ref, format, wrap) {
    const text = ref.invokeMethodAsync("GetCopyText", format);
    writeClipboard(text, wrap)
      .then(() => call(ref, "CopyFinished", format, true, null))
      .catch((error) => call(ref, "CopyFinished", format, false, errorText(error)));
  }

  document.addEventListener("mousedown", (e) => {
    if (e.button !== 0) return;
    const grid = gridOf(e.target);
    const cell = grid ? cellOf(e.target) : null;
    if (!cell) return;

    // The grid selects cells, not text. Without this, a Shift+click or a drag also selects text.
    e.preventDefault();
    const selection = window.getSelection();
    if (selection) selection.removeAllRanges();
    grid.wrap.focus({ preventScroll: true });

    drag = { ref: grid.ref, wrap: grid.wrap, row: cell.row, column: cell.column };
    call(grid.ref, "SelectCell", cell.row, cell.column, e.shiftKey);
  });

  document.addEventListener("mousemove", (e) => {
    if (!drag) return;
    if ((e.buttons & 1) === 0) {
      drag = null;
      return;
    }

    if (!drag.wrap.contains(e.target)) return;
    const cell = cellOf(e.target);
    if (!cell || (cell.row === drag.row && cell.column === drag.column)) return;

    drag.row = cell.row;
    drag.column = cell.column;
    call(drag.ref, "SelectCell", cell.row, cell.column, true);
  });

  document.addEventListener("mouseup", () => {
    drag = null;
  });

  document.addEventListener("contextmenu", (e) => {
    const grid = gridOf(e.target);
    const tr = grid ? e.target.closest("tr[data-r]") : null;
    if (!tr) return;

    e.preventDefault();
    const cell = cellOf(e.target);
    call(grid.ref, "OpenMenu", Number(tr.dataset.r), cell ? cell.column : -1,
      e.clientX, e.clientY, window.innerWidth, window.innerHeight);
  });

  document.addEventListener("keydown", (e) => {
    const grid = gridOf(e.target);
    if (!grid || e.target.matches("input, textarea, [contenteditable='true']")) return;

    const key = e.key.toLowerCase();
    const command = (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey;

    if (command && key === "c") {
      if (grid.wrap.dataset.hasSelection !== "true") return;
      e.preventDefault();
      copy(grid.ref, "text", grid.wrap);
    } else if (command && key === "a") {
      e.preventDefault();
      call(grid.ref, "SelectAll");
    } else if (e.key === "Escape") {
      call(grid.ref, "Escape");
    }
  });

  // The copy items of the grid's menu. The menu is not inside the grid, so each item names its grid.
  document.addEventListener("click", (e) => {
    const item = e.target && e.target.closest ? e.target.closest("[data-grid-copy]") : null;
    if (!item) return;
    const ref = grids.get(item.dataset.gridId);
    if (!ref) return;

    const wrap = document.querySelector(`.results-grid-wrap[data-grid-id="${item.dataset.gridId}"]`);
    copy(ref, item.dataset.gridCopy, wrap);
  });

  return {
    register(id, ref) {
      grids.set(id, ref);
    },

    unregister(id) {
      grids.delete(id);
      if (drag && !document.contains(drag.wrap)) drag = null;
    },
  };
})();
