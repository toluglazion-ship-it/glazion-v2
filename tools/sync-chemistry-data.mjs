#!/usr/bin/env node
// Copies the client's calculator data into the backend so both compute identical UMF results.
//
//   node tools/sync-chemistry-data.mjs <path-to-frontend-checkout>
//
// Writes:
//   src/Glazion.Api/ChemistryData/oxides.json        (generated from packages/chemistry/src/oxides.ts)
//   src/Glazion.Api/ChemistryData/materials.json     (verbatim copy)
//   src/Glazion.Api/ChemistryData/glaze-limits.json  (verbatim copy)
//   tests/Glazion.Api.Tests/ChemistryData/fixtures.json (verbatim copy, for the parity test)
//
// oxides.ts is TypeScript, so it is imported with tsx and its exports are inspected for the oxide table. The
// script refuses to write anything it cannot recognise; it never guesses a molecular weight.
import { execFileSync } from "node:child_process";
import { copyFileSync, existsSync, mkdirSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const client = process.argv[2] && resolve(process.argv[2]);
if (!client) { console.error("usage: node tools/sync-chemistry-data.mjs <path-to-glazion-checkout>"); process.exit(2); }

const pkg = join(client, "packages", "chemistry");
const src = {
  oxides: join(pkg, "src", "oxides.ts"),
  materials: join(pkg, "src", "data", "materials.json"),
  limits: join(pkg, "src", "data", "glaze-limits.json"),
  fixtures: join(pkg, "test", "fixtures.json"),
};
for (const [k, p] of Object.entries(src)) if (!existsSync(p)) { console.error(`missing ${k}: ${p}`); process.exit(1); }

// Import oxides.ts and print every export as JSON.
const probe = `import * as m from ${JSON.stringify(pathToFileURL(src.oxides).href)}; console.log(JSON.stringify(m));`;
const exportsJson = execFileSync("npx", ["--yes", "tsx", "--eval", probe], { cwd: client, encoding: "utf8" });
const exportsObj = JSON.parse(exportsJson);

const SYMBOL = /^[A-Z][a-z]?\d*([A-Z][a-z]?\d*)*$/;
const WEIGHT_KEYS = ["molecularWeight", "molarMass", "mw", "weight", "mass"];
const SYMBOL_KEYS = ["symbol", "formula", "oxide", "id", "key", "name"];
function asTable(value) {
  if (Array.isArray(value)) {
    const rows = value.map((e) => {
      if (!e || typeof e !== "object") return null;
      const sk = SYMBOL_KEYS.find((k) => typeof e[k] === "string" && SYMBOL.test(e[k]));
      const wk = WEIGHT_KEYS.find((k) => typeof e[k] === "number");
      return sk && wk ? [e[sk], e[wk]] : null;
    });
    return rows.every(Boolean) ? rows : null;
  }
  if (value && typeof value === "object") {
    const rows = Object.entries(value).map(([k, v]) => {
      if (!SYMBOL.test(k)) return null;
      if (typeof v === "number") return [k, v];
      const wk = v && typeof v === "object" && WEIGHT_KEYS.find((w) => typeof v[w] === "number");
      return wk ? [k, v[wk]] : null;
    });
    return rows.every(Boolean) ? rows : null;
  }
  return null;
}
const candidates = Object.entries(exportsObj)
  .map(([name, v]) => [name, asTable(v), Array.isArray(v)])
  .filter(([, t]) => t && t.some(([s]) => s === "SiO2"));
// oxides.ts may export the same table more than once (OXIDES array and OXIDE_INDEX lookup). Accept that only
// when every candidate holds identical symbol → weight pairs; prefer an array, which keeps declaration order.
const key = (t) => JSON.stringify([...t].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
if (candidates.length === 0 || new Set(candidates.map(([, t]) => key(t))).size !== 1) {
  console.error(`expected one oxide table in oxides.ts (or several identical ones), found: ${candidates.map(([n]) => n).join(", ") || "none"}`);
  console.error(`exports seen: ${Object.keys(exportsObj).join(", ")}`);
  process.exit(1);
}
const [exportName, table] = candidates.find(([, , isArray]) => isArray) ?? candidates[0];
const sio2 = table.find(([s]) => s === "SiO2")[1];
if (table.length !== 31 || sio2 !== 60.0843) {
  console.error(`oxide table '${exportName}' has ${table.length} oxides and SiO2 = ${sio2}; the calculator doc says 31 oxides and SiO2 60.0843. Not writing.`);
  process.exit(1);
}

const apiData = join(repoRoot, "src", "Glazion.Api", "ChemistryData");
const testData = join(repoRoot, "tests", "Glazion.Api.Tests", "ChemistryData");
mkdirSync(apiData, { recursive: true });
mkdirSync(testData, { recursive: true });
writeFileSync(join(apiData, "oxides.json"), JSON.stringify(Object.fromEntries(table), null, 2) + "\n");
copyFileSync(src.materials, join(apiData, "materials.json"));
copyFileSync(src.limits, join(apiData, "glaze-limits.json"));
copyFileSync(src.fixtures, join(testData, "fixtures.json"));
const sha = execFileSync("git", ["-C", client, "rev-parse", "HEAD"], { encoding: "utf8" }).trim();
writeFileSync(join(apiData, "SOURCE.txt"), `Copied from Realdiamond/glazion packages/chemistry at ${sha} (oxides from export '${exportName}').\n`);
console.log(`synced ${table.length} oxides from '${exportName}', materials, limits and fixtures from ${sha}`);
