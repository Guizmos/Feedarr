function readDefaultBasename() {
  if (
    typeof import.meta !== "undefined"
    && typeof import.meta.env !== "undefined"
    && typeof import.meta.env.BASE_URL === "string"
    && import.meta.env.BASE_URL
  ) {
    return import.meta.env.BASE_URL;
  }
  return "/";
}

export function normalizeBasePath(basename = readDefaultBasename()) {
  const raw = String(basename || "/").trim();
  if (!raw) return "/";

  let path = raw;
  if (!path.startsWith("/")) path = `/${path}`;
  path = path.replace(/\/{2,}/g, "/");
  if (path.length > 1) path = path.replace(/\/+$/, "");
  return path || "/";
}

export function normalizePath(pathname, basename = readDefaultBasename()) {
  const raw = String(pathname || "/").trim();
  if (!raw) return "/";

  const withoutHash = raw.split("#", 1)[0];
  const withoutQuery = withoutHash.split("?", 1)[0];
  let path = withoutQuery || "/";

  if (!path.startsWith("/")) path = `/${path}`;
  path = path.replace(/\/{2,}/g, "/");

  const normalizedBase = normalizeBasePath(basename);
  if (normalizedBase !== "/") {
    if (path === normalizedBase) {
      path = "/";
    } else if (path.startsWith(`${normalizedBase}/`)) {
      path = path.slice(normalizedBase.length);
      if (!path.startsWith("/")) path = `/${path}`;
    }
  }

  if (path.length > 1) path = path.replace(/\/+$/, "");
  return path || "/";
}

export function matchRoute(pathname, matcher) {
  const normalizedPath = normalizePath(pathname, "/");
  if (typeof matcher === "string") {
    return normalizedPath === normalizePath(matcher, "/");
  }
  if (!matcher || typeof matcher !== "object") return false;

  const target = normalizePath(matcher.path || "/", "/");
  const prefix = matcher.prefix !== false;
  if (!prefix) return normalizedPath === target;
  return normalizedPath === target || normalizedPath.startsWith(`${target}/`);
}

