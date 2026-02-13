import { readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const root = resolve(process.cwd(), "src");
const testFiles = [];

function collectTests(dir) {
  const entries = readdirSync(dir, { withFileTypes: true });
  for (const entry of entries) {
    const fullPath = join(dir, entry.name);
    if (entry.isDirectory()) {
      collectTests(fullPath);
      continue;
    }
    if (entry.isFile() && entry.name.endsWith(".test.mjs")) {
      testFiles.push(fullPath);
    }
  }
}

collectTests(root);
testFiles.sort((a, b) => a.localeCompare(b));

for (const file of testFiles) {
  await import(pathToFileURL(file).href);
}
