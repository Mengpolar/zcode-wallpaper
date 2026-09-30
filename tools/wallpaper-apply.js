// Canonical wallpaper apply function. The C# injector embeds this source,
// substituting __CONFIG__ with the JSON config. Idempotent by design.
(function () {
  function apply(cfg) {
    if (!cfg || !cfg.src) return "no config";
    var old = document.getElementById("zcode-wallpaper");
    if (old) old.remove();
    if (!document.documentElement) return "no document";
    var bg = document.createElement("div");
    bg.id = "zcode-wallpaper";
    bg.style.cssText =
      "position:fixed;inset:0;z-index:2147483647;pointer-events:none;" +
      "background-image:url(\"" + cfg.src + "\");" +
      "background-size:" + (cfg.fit || "cover") + ";" +
      "background-position:center " + (cfg.posY || "center") + ";" +
      "background-repeat:no-repeat;" +
      "opacity:" + (cfg.opacity != null ? cfg.opacity : 0.3) + ";" +
      "filter:blur(" + (cfg.blur || 0) + "px) brightness(" +
        (cfg.brightness != null ? cfg.brightness : 1) + ");" +
      "transform:scale(" + (cfg.scale || 1.05) + ");";
    document.documentElement.appendChild(bg);
    return "ok";
  }
  if (typeof window !== "undefined") window.__ZCW_APPLY__ = apply;
  if (document.documentElement) {
    apply(__CONFIG__);
  } else {
    document.addEventListener("DOMContentLoaded", function () {
      apply(__CONFIG__);
    });
  }
})();
