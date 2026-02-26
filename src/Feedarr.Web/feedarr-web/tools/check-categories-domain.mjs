import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";

const SRC_ROOT = resolve(process.cwd(), "src");
const extensions = new Set([".js", ".jsx", ".mjs", ".ts", ".tsx"]);

const allowLegacyLiteralFiles = new Set([
  "src/domain/categories/categoryGroups.constants.js",
  "src/domain/categories/__tests__/categoryDomain.test.mjs",
  "src/services/CategoryService.js",
]);

const rules = [
  {
    name: "UNIFIED_PRIORITY",
    regex: /\bUNIFIED_PRIORITY\b/g,
    allowlist: new Set(),
    reason: "UNIFIED_PRIORITY must be centralized in src/domain/categories/categoryPriority.constants.js",
  },
  {
    name: "FEEDARR_GROUPS",
    regex: /\bFEEDARR_GROUPS\b/g,
    allowlist: new Set(),
    reason: "FEEDARR_GROUPS must not be reintroduced (use CATEGORY_GROUPS from domain)",
  },
  {
    name: "legacy-literals",
    regex: /(['"`])(shows|show|film|serie|other)\1/g,
    allowlist: allowLegacyLiteralFiles,
    reason:
      "Legacy literals are only allowed in categoryGroups.constants aliases or explicit legacy compatibility files",
  },
];

function isSourceFile(path) {
  for (const ext of extensions) {
    if (path.endsWith(ext)) return true;
  }
  return false;
}

function walk(dir, output) {
  const entries = readdirSync(dir, { withFileTypes: true });
  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(full, output);
      continue;
    }
    if (!entry.isFile()) continue;
    if (!isSourceFile(full)) continue;
    output.push(full);
  }
}

function indexToLineCol(text, index) {
  let line = 1;
  let col = 1;
  for (let i = 0; i < index; i += 1) {
    if (text[i] === "\n") {
      line += 1;
      col = 1;
    } else {
      col += 1;
    }
  }
  return { line, col };
}

const files = [];
walk(SRC_ROOT, files);

const violations = [];

for (const absolutePath of files) {
  const relPath = relative(process.cwd(), absolutePath).replace(/\\/g, "/");
  const content = readFileSync(absolutePath, "utf8");

  for (const rule of rules) {
    if (rule.allowlist.has(relPath)) continue;

    rule.regex.lastIndex = 0;
    let match = rule.regex.exec(content);
    while (match) {
      const { line, col } = indexToLineCol(content, match.index);
      violations.push({
        rule: rule.name,
        reason: rule.reason,
        file: relPath,
        line,
        col,
        snippet: match[0],
      });
      match = rule.regex.exec(content);
    }
  }
}

if (violations.length > 0) {
  console.error("[categories-domain] violations detected:");
  for (const violation of violations) {
    console.error(
      `- ${violation.file}:${violation.line}:${violation.col} [${violation.rule}] ${violation.snippet} -> ${violation.reason}`
    );
  }
  process.exit(1);
}

console.log("[categories-domain] OK");

