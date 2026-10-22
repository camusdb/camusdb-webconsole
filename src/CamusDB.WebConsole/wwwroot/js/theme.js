/*
 * Switches the page between the dark and the light theme. App.razor reads the cookie on the next
 * load, so the server renders the chosen theme and the page does not flash the other one first.
 * MainLayout calls this; the MudBlazor palette follows from its own IsDarkMode.
 */
window.camusTheme = {
  set: function (name) {
    const mode = name === "light" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", mode);

    const secure = location.protocol === "https:" ? "; secure" : "";
    document.cookie = "camus-theme=" + mode + "; path=/; max-age=31536000; samesite=lax" + secure;

    if (window.camusSql)
      window.camusSql.syncTheme();
  }
};
