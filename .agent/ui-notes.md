# UI findings

Things about the Cities: Skylines II frontend that cost real debugging time. This is not
the changelog: it records why something was hard, not what changed.

## A `.css` shipped beside the `.mjs` is never loaded

**Symptom.** Every rule in `zone-editor.css` was silently inert in game. The two-choice
selectors rendered as one run of text — "THIS STOPWHOLE LINE", "GLOBALLINE COLOUR" —
because `.segment`'s padding, `min-width` and background never applied, and the buttons
that set `text-transform: none` still rendered uppercase. The class names in the bundle
were correct; the rules behind them simply did not exist in the document.

**Cause.** `Game.Modding.ModManager.InitializeUIModules` (decompiled `Game.Modding/ModManager.cs`,
lines 461-473) collects `UIModuleAsset` instances, and `UIModuleAsset.kExtension` is
`".mjs"` — nothing else. It registers `Path.GetDirectoryName(asset.path)` as the `ui-mods`
host location and passes only `asset.couiPath` (the `.mjs`) to
`appBindings.AddActiveUIModLocation`. `Colossal.IO.AssetDatabase/FileAsset.kExtensions`
does include `".css"`, so a stylesheet in the mod folder is *served* over
`coui://ui-mods/`, which is what makes this look plausible — but nothing ever links it
into the document. There is no stylesheet-injection path anywhere in the managed code: a
search of the decompiled source for `StyleSheet`, `InjectCss`, `AddStyle` and `LoadStyle`
returns nothing.

**Consequence for the build.** `mini-css-extract-plugin` is exactly the wrong plugin here.
The build succeeded, the file appeared in `dist/`, the csproj copied it, `verify-release.ps1`
confirmed it was present and non-empty, and the packaged mod shipped a stylesheet the game
would never read. Every gate passed on a file that did nothing.

**Fix.** `css-loader` alone, so the CSS text and the CSS-module class map both travel
inside the `.mjs` (`sheet.locals` for the names, `String(sheet)` for the text), and
`src/index.js` injects a `<style>` element on registration, keyed by a fixed element id so
re-registration replaces rather than stacks.

**Specificity.** The game's own stylesheets can be inserted after ours, so rules that have
to beat a game theme class (`text-transform`, padding, background on a themed `Button`)
double their class name — `.segment.segment` — rather than relying on insertion order.

## Two labels in one button is not a two-choice selector

The original "This stop / Whole line" and "Global / Line colour" controls were a single
`Button` containing two `<span>`s, with `onSelect` toggling. Pressing the already-active
choice flipped to the other one, and with the styles missing the two labels concatenated.
They are now one `Button` per choice inside a flex row, each handler idempotent
(`setLineColor(false)` / `setLineColor(true)`, not a toggle).

Note the third colour-source state: `BoardingZoneCustomColor` on the stop means neither
"Global" nor "Line colour" is active. That is correct — and pressing either one is the
only way back, because `SetLineColor` removes the custom-colour component.

## `moduleRegistry.get` throws

It throws when the module or the export is missing, and registration runs outside any
React boundary, so one renamed module path in a game patch takes down the whole UI. Every
lookup is wrapped, and the section is wrapped in an error boundary so a render failure
hides the mod's panel instead of leaving the player on a bare map.
