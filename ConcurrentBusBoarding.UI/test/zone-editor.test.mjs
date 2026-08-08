import assert from "node:assert/strict";
import { readFile, access } from "node:fs/promises";

const distUrl = new URL("../dist/", import.meta.url);
const moduleText = await readFile(new URL("ConcurrentBusBoarding.mjs", distUrl), "utf8");

assert.match(moduleText, /ConcurrentBusBoarding/);
assert.match(moduleText, /zoneEditor/);
assert.match(moduleText, /setZone/);
assert.match(moduleText, /setLineColor/);
assert.match(moduleText, /setGlobalOverlayColor/);
assert.match(moduleText, /setStopOverlayColor/);
assert.match(moduleText, /ColorCustomizeField/);
assert.match(moduleText, /optionsWidgetComponents/);
assert.match(moduleText, /ChooseGlobalOverlayColor/);
assert.match(moduleText, /resetZone/);
assert.match(moduleText, /toggleZoneEditing/);
assert.match(moduleText, /Edit on map/);
assert.match(moduleText, /Right-click or Esc/);
assert.match(moduleText, /Cyan rear corners resize/);
assert.match(moduleText, /Whole line/);
assert.match(moduleText, /This stop/);
assert.match(moduleText, /Colour source/);
assert.match(moduleText, /Line colour/);
assert.match(moduleText, /Game\.UI\.InGame\.LinesSection/);

// The game registers only the .mjs of a UI module as a UI mod location, so anything
// emitted beside it is never loaded. The stylesheet has to travel inside the bundle
// and be injected at runtime, or every rule in it is silently dropped and the panel
// renders as unstyled text.
assert.match(moduleText, /segmentedGroup/, "CSS-module class map should be bundled");
assert.match(moduleText, /segmentActive/, "CSS-module class map should be bundled");
assert.match(moduleText, /padding-top:\s*8rem/, "CSS text should be bundled, not extracted");
assert.match(moduleText, /createElement\("style"\)/, "styles should be injected at runtime");
assert.match(moduleText, /concurrent-bus-boarding-zone-editor-styles/,
  "the injected style element should be identifiable so injection stays idempotent");

// A single button holding both choice labels was what rendered as "THIS STOPWHOLE LINE".
// Each choice must be its own element.
const segmentUses = moduleText.match(/segment\b/g) || [];
assert.ok(segmentUses.length > 0, "segment class should be referenced");
assert.doesNotMatch(moduleText, /segmentedButton/,
  "the merged two-label button should be gone");

let extractedCss = true;
try {
  await access(new URL("ConcurrentBusBoarding.css", distUrl));
} catch {
  extractedCss = false;
}
assert.equal(extractedCss, false,
  "no separate .css should be emitted: the game never loads it");

console.log("Zone editor UI smoke check passed.");
