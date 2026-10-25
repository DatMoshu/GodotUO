"use strict";
(async () => {
  const status = document.getElementById("release-status");
  const target = document.getElementById("releases");
  const safeURL = value => {
    const url = new URL(value);
    if (url.origin !== "https://github.com" || !url.pathname.startsWith("/DatMoshu/GodotUO/")) throw new Error("Unexpected release URL");
    return url.href;
  };
  try {
    const response = await fetch("https://api.github.com/repos/DatMoshu/GodotUO/releases?per_page=10", {signal: AbortSignal.timeout(10000)});
    if (!response.ok) throw new Error("Release list unavailable");
    const releases = (await response.json()).filter(release => !release.draft);
    const fragment = document.createDocumentFragment();
    for (const release of releases) {
      const article = document.createElement("article");
      const heading = document.createElement("h2");
      heading.textContent = release.name || release.tag_name;
      article.append(heading);
      const label = document.createElement("p");
      label.textContent = release.prerelease ? "Prerelease · for testing" : "Release";
      article.append(label);
      const notes = document.createElement("a");
      notes.textContent = "Read release notes";
      notes.href = safeURL(release.html_url);
      article.append(notes);
      const list = document.createElement("ul");
      for (const asset of release.assets) {
        const item = document.createElement("li");
        const link = document.createElement("a");
        link.textContent = asset.name;
        link.href = safeURL(asset.browser_download_url);
        item.append(link);
        list.append(item);
      }
      if (!release.assets.length) {
        const empty = document.createElement("p");
        empty.textContent = "No packaged downloads are attached to this release.";
        article.append(empty);
      }
      article.append(list);
      fragment.append(article);
    }
    target.replaceChildren(fragment);
    status.textContent = releases.length ? "Published releases, newest first." : "No releases have been published yet. You can build from source or try a development build below.";
  } catch {
    status.textContent = "The release list could not load. Open GitHub Releases below to check available downloads.";
  }
})();
