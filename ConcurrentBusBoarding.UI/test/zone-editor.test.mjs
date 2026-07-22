import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const moduleText = await readFile(new URL("../dist/ConcurrentBusBoarding.mjs", import.meta.url), "utf8");
const cssText = await readFile(new URL("../dist/ConcurrentBusBoarding.css", import.meta.url), "utf8");

assert.match(moduleText, /ConcurrentBusBoarding/);
assert.match(moduleText, /zoneEditor/);
assert.match(moduleText, /setZone/);
assert.match(moduleText, /resetZone/);
assert.match(moduleText, /toggleZoneEditing/);
assert.match(moduleText, /Edit on map/);
assert.match(moduleText, /Right-click or Esc/);
assert.match(moduleText, /Cyan rear corners resize/);
assert.match(moduleText, /Game\.UI\.InGame\.LinesSection/);
assert.ok(cssText.length > 0, "zone editor CSS should be emitted");
console.log("Zone editor UI smoke check passed.");
