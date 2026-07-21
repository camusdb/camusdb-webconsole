window.camusStorage = {
  getJson: function (key) {
    try {
      const raw = localStorage.getItem(key);
      if (raw == null || raw === "")
        return null;
      return JSON.parse(raw);
    } catch {
      return null;
    }
  },

  setJson: function (key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
      return true;
    } catch {
      return false;
    }
  }
};
