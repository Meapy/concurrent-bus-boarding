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

## The rear-geometry walk is the click cost — and the first fix for it was wrong

**This section corrects the one below it.** The whole-city scan described there is real and worth
removing, but it was *not* what the player was feeling. A build with that fix deployed still stalled
for about a second on selecting a stop, and the stall disappeared with the mod disabled. The lesson
is the one already written at the bottom of the section below and not acted on: the conclusion was
inferred, an inferred conclusion was shipped, and the residue was left for the player to find.

**Actual cause.** `BuildZonePieces` walks the route's segments backwards from the stop collecting
contiguous lane pieces. Its loop condition was:

```
for (offset = 1; offset <= segments.Length && available < BoardingPolicy.MaximumCustomZoneLength; offset++)
```

Two independent problems in one line.

1. `MaximumCustomZoneLength` is 200 m — the largest a zone can *ever* be. An ordinary automatic stop
   displays 26 m, so the walk kept collecting long after it had everything it could show.
2. `available` only grows when a piece is actually appended, and a piece is appended only if it is
   within 12 m of the previous piece's rear. The moment the contiguous chain breaks — a junction the
   walk cannot follow — `available` stops growing, the condition becomes permanently unsatisfiable,
   and the loop continues through *every remaining segment of the entire route*, reading each one's
   full `PathElement` buffer and calling `TryGetLaneGeometry` (up to four component lookups plus
   three bezier evaluations) on every element.

`TryGetStopZone` runs this per connected route on the stop, on the frame the stop is selected, on the
main thread. On a long line with large path buffers that is tens of thousands of managed ECS lookups.

**Why the earlier fix missed it.** That work restricted *which stops* get resolved. It never touched
what resolving *one* stop costs, and selecting a stop resolves exactly one — the on-demand
`TryGetObservedZone(selected)` call, which was not part of the scan that got restricted.

**Fix.** Bound the walk by `GetRequestedZoneLength(zone) + 12 m` rather than the global maximum, which
required moving `ApplyOverride` ahead of `BuildZonePieces` so the budget knows about a length
override. Break out of the segment loop once the chain is established and a whole segment contributes
nothing, since every segment after it is further from the stop and cannot attach. Hard-cap total
elements examined as a backstop.

**The interaction that bound has with the slider.** A custom stop keeps the full 200 m budget, because
its slider can be dragged out without geometry being re-resolved. An automatic stop therefore has to
re-resolve once, on the transition to custom — handled in both `SetZone` and the map-drag path, and
only on that transition, or the drag is back to rebuilding every frame.

**Also fixed here.** A failed resolution is not stored in `m_Zones`, so a selected stop that cannot be
resolved yet — the "Waiting for a bus to approach" state — re-ran the entire walk every frame it
stayed selected. There is now a negative cache cleared by the periodic refresh. And
`TryGetObservedZone(selected)` was called unconditionally each frame, walking every piece of the
already-cached zone a second time, since `DrawZone` validates it too.

**Measurement.** A `CBB_DIAGNOSTICS` breadcrumb now logs elements examined, pieces collected and the
budget for each walk. Build with `-p:CbbDiagnostics=true` and read
`ConcurrentBusBoarding-breadcrumbs.log` if this needs checking again rather than reasoning about.

## The whole-city scan (real, but not what the player felt)

**Symptom.** A frame drop on clicking a bus stop, and a much worse sustained stutter while dragging
the **Length** slider.

**Cause.** `BoardingZoneRenderSystem.OnUpdate` refreshed on a 60-frame timer, but the countdown sits
behind `anythingToDraw &&`, so it does not advance while nothing is selected. In the default
selected-only display mode that means `m_RefreshIn` was almost always already at zero by the time the
player clicked, and the refresh landed on the selection frame — which looked like selection causing
it. The refresh itself called `FindObservedZones` over *every* public transport vehicle in the city,
resolving each one's stop geometry by walking route lanes and inbound path elements, allocating a
fresh `Dictionary` for the result, and then drawing at most two stops.

The slider was worse. `BoardingZoneEditorUISystem.SetZone` called `Refresh()`, which called
`m_RenderSystem.Invalidate()` and set `m_RefreshIn = 0`, and the UI fires `setZone` on every frame of
a drag. So the whole-city rebuild ran once per frame for as long as the handle was held — and it
could never have been necessary, because a length override is read from its component by
`ApplyOverride` on every draw and does not touch the cached lane pieces at all.

**Fix.** `FindObservedZones` takes up to two stops to restrict to and fills a reused dictionary;
selected-only mode passes the selected and editing stops. `TryGetStop` runs before `IsBus` because it
is the cheaper rejection. Selection changes now force the (cheap) refresh explicitly instead of
relying on the frozen countdown. `Refresh()` is split into `RefreshBinding`, `RefreshColors` and
`RefreshGeometry` so each trigger invalidates only what it can actually affect.

**Two smaller things found alongside.** `GetOverlayColor` walks the stop's `ConnectedRoute` buffer up
to twice and was called per drawn zone per frame; it is now memoized until a colour changes or the
periodic refresh falls due, which keeps the existing "may lag by at most one second" contract. And
`m_Zones` was unbounded — HANDOVER already flagged this — so it is capped, evicting stops that are
not on screen. Evicting a cached *physical* observation is a real cost, since it is what keeps paired
pull-in bays on their own side, but it is re-learned the next time a bus stops there.

**Not measured.** This is a static reading of the code, not a profiler capture. The claim that the
whole-city scan dominates rests on it being O(vehicles) with route-buffer walks per vehicle against
O(1) drawn stops; if a spike survives these changes, profile before changing more.

## An InfoRow's right slot holds exactly one focusable child

Replacing the merged two-label button with one game `Button` per choice introduced this, and it only
showed up in the UI log:

```
Cannot register second focus key 'Button:910272'! PassThroughFocusControllers can only host a single child.
Registered key: Button:741489 <-- PassThroughFocusController
```

followed on teardown by `Attempted to unregister mismatching focus key`. It fires in pairs — two
segmented rows, two buttons each — on every selection, and each one captures a full managed stack
through `CoLogHandler`, so it is not free.

The sentinel that opts a `Button` out of focus registration lives in a module under
`game-ui/common/focus/`, whose exact path and export name cannot be read from disk. It is discovered
with `moduleRegistry.find(/common\/focus\//i)` and the matches are logged, so the next log pins the
real values. When nothing is found the segments render as plain elements carrying their own pill
styling, which register no focus key and therefore cannot reproduce the error.

**Coherent GT is not Chromium.** The same log shows it rejecting `gap`, `inset`, `word-wrap`,
`object-fit`, `align-items: start`, `hsla()` in shorthands, and `var()` inside shorthand properties.
None of those are in this mod's stylesheet, and the smoke test now asserts they stay out. If a rule
silently does nothing, check the UI log for `Unsupported CSS property detected` before assuming the
stylesheet did not load.

## `moduleRegistry.get` throws

It throws when the module or the export is missing, and registration runs outside any
React boundary, so one renamed module path in a game patch takes down the whole UI. Every
lookup is wrapped, and the section is wrapped in an error boundary so a render failure
hides the mod's panel instead of leaving the player on a bare map.
