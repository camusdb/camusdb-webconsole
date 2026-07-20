window.camusDownload = {
  downloadText: function (filename, content, mimeType) {
    const blob = new Blob([content], { type: mimeType || "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename || "download.txt";
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }
};

window.camusInput = {
  /**
   * Block non-integer keystrokes/pastes on inputs marked data-camus-integer="1".
   * Allows an optional leading '-' and digits only.
   */
  bindIntegerInputs: function (root) {
    const scope = root || document;
    const inputs = scope.querySelectorAll("input[data-camus-integer='1']");
    inputs.forEach(function (el) {
      if (el._camusIntegerBound) return;
      el._camusIntegerBound = true;

      function normalizeInteger(value) {
        if (!value) return "";
        let out = "";
        for (let i = 0; i < value.length; i++) {
          const c = value[i];
          if (c >= "0" && c <= "9") out += c;
          else if (c === "-" && out.length === 0) out += c;
        }
        return out;
      }

      el.addEventListener("beforeinput", function (e) {
        if (!e.inputType || e.inputType.startsWith("delete") || e.inputType === "historyUndo" || e.inputType === "historyRedo")
          return;

        const data = e.data;
        if (data == null) return;

        const start = el.selectionStart ?? 0;
        const end = el.selectionEnd ?? 0;
        const next = el.value.slice(0, start) + data + el.value.slice(end);
        if (normalizeInteger(next) !== next)
          e.preventDefault();
      });

      el.addEventListener("paste", function (e) {
        e.preventDefault();
        const text = (e.clipboardData || window.clipboardData).getData("text") || "";
        const start = el.selectionStart ?? 0;
        const end = el.selectionEnd ?? 0;
        const next = normalizeInteger(el.value.slice(0, start) + text + el.value.slice(end));
        el.value = next;
        el.dispatchEvent(new Event("input", { bubbles: true }));
      });
    });
  }
};
