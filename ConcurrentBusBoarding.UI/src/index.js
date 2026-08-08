import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import sheet from "./zone-editor.css";

// css-loader's default export carries both the CSS text (toString) and the CSS-module
// class map (locals). Nothing extracts it to a file any more: the game registers only
// the .mjs of a UI module as a UI mod location, so a sibling .css is served but never
// linked into the document. The rules are injected below instead.
const styles = (sheet && sheet.locals) || {};
const styleElementId = "concurrent-bus-boarding-zone-editor-styles";

const injectStyles = () => {
  if (typeof document === "undefined" || !sheet)
    return;
  let element = document.getElementById(styleElementId);
  if (!element) {
    element = document.createElement("style");
    element.id = styleElementId;
    element.type = "text/css";
    (document.head || document.documentElement).appendChild(element);
  }
  const css = String(sheet);
  if (element.textContent !== css)
    element.textContent = css;
};

const cx = (...names) => names.filter(Boolean).join(" ");

const zoneEditor$ = bindValue("ConcurrentBusBoarding", "zoneEditor", {
  visible: false,
  available: false,
  customized: false,
  forceGlobal: false,
  customStopColor: false,
  hasLine: false,
  globalColor: { r: 0.15, g: 0.55, b: 0.95, a: 0.18 },
  stopColor: { r: 0.15, g: 0.55, b: 0.95, a: 0.18 },
  routeColor: { r: 0.15, g: 0.55, b: 0.95, a: 0.18 },
  editing: false,
  offset: 0,
  length: 26
});
const selectedInfoSectionsModule = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const linesSectionType = "Game.UI.InGame.LinesSection";
const sliderModule = "game-ui/common/input/slider/slider.tsx";
const infoSectionModule = "game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx";
const infoRowModule = "game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx";
const buttonModule = "game-ui/common/input/button/button.tsx";
const secondaryButtonThemeModule = "game-ui/common/input/button/themes/paradox-secondary-button.module.scss";
const colorFieldModule = "game-ui/common/input/color-picker/color-field/color-field.tsx";
const optionFieldModule = "game-ui/menu/widgets/field/field.tsx";
const optionWidgetRendererModule = "game-ui/menu/widgets/option-widget-renderer.tsx";
const widgetBindingsModule = "game-ui/widgets/data-binding/widget-bindings.ts";
const editorMarker = Symbol.for("ConcurrentBusBoarding.ZoneEditor");
const settingsMarker = Symbol.for("ConcurrentBusBoarding.GlobalColorSetting");

const toRgbHex = (color) => ["r", "g", "b"]
  .map((channel) => Math.round(Math.max(0, Math.min(1, color[channel])) * 255)
    .toString(16).padStart(2, "0"))
  .join("");

// moduleRegistry.get throws when a module or export is missing, and this runs during
// registration, so an unguarded lookup after a game patch takes the whole UI down.
const lookup = (moduleRegistry, module, exportName) => {
  try {
    const value = moduleRegistry.get(module, exportName);
    if (value == null)
      console.warn(`[Concurrent Bus Boarding] ${exportName} is missing from ${module}.`);
    return value;
  } catch (error) {
    console.warn(`[Concurrent Bus Boarding] Could not read ${exportName} from ${module}: ${error}`);
    return null;
  }
};

const quietLookup = (moduleRegistry, module, exportName) => {
  try {
    return moduleRegistry.get(module, exportName);
  } catch (error) {
    return undefined;
  }
};

// An InfoRow's right slot is a PassThroughFocusController, which hosts exactly one focusable child.
// Two game Buttons in it make the focus system log "Cannot register second focus key" on every
// render and a matching "Attempted to unregister mismatching focus key" on teardown, each with a
// full managed stack capture. The sentinel that opts a Button out of focus registration lives in a
// module whose path cannot be read from disk, so it is discovered and the matches are logged: if
// this returns nothing, the segments fall back to non-focusable elements, which cannot trigger it.
const findDisabledFocusKey = (moduleRegistry) => {
  let matches = [];
  try {
    matches = moduleRegistry.find(/common\/focus\//i) || [];
  } catch (error) {
    console.warn(`[Concurrent Bus Boarding] Could not search for the focus module: ${error}`);
  }
  const paths = (Array.isArray(matches) ? matches : [])
    .map((entry) => (typeof entry === "string" ? entry : entry && (entry.id || entry.path)))
    .filter(Boolean);
  console.log(`[Concurrent Bus Boarding] focus modules: ${paths.join(", ") || "none found"}`);
  for (const path of paths) {
    for (const name of ["FOCUS_DISABLED", "FOCUS_KEY_DISABLED", "DISABLED"]) {
      const value = quietLookup(moduleRegistry, path, name);
      if (value !== undefined && value !== null) {
        console.log(`[Concurrent Bus Boarding] using ${name} from ${path} for segment focus.`);
        return value;
      }
    }
  }
  console.warn("[Concurrent Bus Boarding] No disabled-focus sentinel found; " +
    "rendering choice segments as non-focusable elements instead.");
  return null;
};

// An undefined component thrown during render clears the entire game UI and leaves the
// player on a bare map, so the injected section renders nothing rather than throwing.
class EditorBoundary extends React.Component {
  constructor(props) {
    super(props);
    this.state = { failed: false };
  }

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error) {
    console.error(`[Concurrent Bus Boarding] Zone editor UI failed and was hidden: ${error}`);
  }

  render() {
    return this.state.failed ? null : this.props.children;
  }
}

export default function register(moduleRegistry) {
  injectStyles();
  console.log("[Concurrent Bus Boarding] UI module registered.");
  const Slider = lookup(moduleRegistry, sliderModule, "Slider");
  const useStepTransformer = lookup(moduleRegistry, sliderModule, "useStepTransformer");
  const InfoSection = lookup(moduleRegistry, infoSectionModule, "InfoSection");
  const InfoRow = lookup(moduleRegistry, infoRowModule, "InfoRow");
  const Button = lookup(moduleRegistry, buttonModule, "Button");
  const secondaryButtonTheme = lookup(moduleRegistry, secondaryButtonThemeModule, "classes");
  const ColorCustomizeField = lookup(moduleRegistry, colorFieldModule, "ColorCustomizeField");
  const OptionField = lookup(moduleRegistry, optionFieldModule, "OptionField");
  const WidgetType = lookup(moduleRegistry, widgetBindingsModule, "WidgetType");
  const disabledFocusKey = findDisabledFocusKey(moduleRegistry);

  // Each choice is its own control, so pressing the choice that is already active is a no-op
  // instead of flipping to the other one. A single button holding two labels renders them as one
  // run of text when the styles are unavailable, which is what "THIS STOPWHOLE LINE" was.
  //
  // Two focusable children cannot share an InfoRow's right slot, so either the game Button opts out
  // of focus registration or the segments are plain elements. Plain elements lose the theme's hover
  // and press feedback, so they carry their own.
  const Segment = ({ option, selected, onChange }) => {
    const className = cx(styles.segment, selected && styles.segmentActive,
      option.disabled === true && styles.segmentDisabled,
      !disabledFocusKey && styles.segmentPlain);
    if (disabledFocusKey) {
      return React.createElement(Button, {
        theme: secondaryButtonTheme,
        className,
        focusKey: disabledFocusKey,
        disabled: option.disabled === true,
        onSelect: () => onChange(option.value)
      }, option.label);
    }
    return React.createElement("div", {
      className,
      onClick: option.disabled === true ? undefined : () => onChange(option.value)
    }, option.label);
  };

  const Segmented = ({ options, value, onChange }) => React.createElement(
    "div",
    { className: styles.segmentedGroup },
    options.map((option) => React.createElement(Segment, {
      key: String(option.value),
      option,
      selected: option.value === value,
      onChange
    }))
  );

  const GlobalColorSetting = (props) => {
    const zone = useValue(zoneEditor$);
    const changeColor = (color) =>
      trigger("ConcurrentBusBoarding", "setGlobalOverlayColor", toRgbHex(color));
    return React.createElement(OptionField, {
      id: props.path,
      label: "Global overlay colour",
      warning: props.props.warning,
      disabled: props.props.disabled
    }, React.createElement(ColorCustomizeField, {
      value: zone.globalColor,
      onChange: changeColor,
      className: styles.colorField
    }));
  };

  if (OptionField && ColorCustomizeField && WidgetType) {
    moduleRegistry.extend(optionWidgetRendererModule, "optionsWidgetComponents", (components) => {
      const OriginalButton = components[WidgetType.Button];
      if (!OriginalButton || OriginalButton[settingsMarker])
        return components;
      const ButtonWithGlobalColor = (props) =>
        String(props.path).endsWith("ChooseGlobalOverlayColor")
          ? React.createElement(GlobalColorSetting, props)
          : React.createElement(OriginalButton, props);
      ButtonWithGlobalColor[settingsMarker] = true;
      components[WidgetType.Button] = ButtonWithGlobalColor;
      return components;
    });
  }

  const withZoneEditor = (OriginalLinesSection) => {
    if (OriginalLinesSection[editorMarker])
      return OriginalLinesSection;

    const ZoneEditor = () => {
      const zone = useValue(zoneEditor$);
      const [length, setLength] = React.useState(zone.length ?? 26);
      const [wholeLine, setWholeLine] = React.useState(false);
      const oneMetreSteps = useStepTransformer(1);
      // "Whole line" is meaningless without a served line, so a stop that loses its
      // line falls back to itself rather than editing a route that is not there.
      const scope = zone.hasLine && wholeLine ? "line" : "stop";
      const colourSource = zone.customStopColor
        ? "custom"
        : (zone.forceGlobal ? "global" : "line");

      React.useEffect(() => {
        setLength(zone.length ?? 26);
      }, [zone.length, zone.customized]);
      const changeLength = (value) => {
        setLength(value);
        trigger("ConcurrentBusBoarding", "setZone", 0, value);
      };
      const reset = () => trigger("ConcurrentBusBoarding", "resetZone");
      const setColourSource = (source) =>
        trigger("ConcurrentBusBoarding", "setLineColor", source === "line");
      const changeStopColor = (color) =>
        trigger("ConcurrentBusBoarding", "setStopOverlayColor", toRgbHex(color), scope === "line");
      const toggleEditing = () => trigger("ConcurrentBusBoarding", "toggleZoneEditing");

      if (!zone.visible)
        return null;

      return React.createElement(
        InfoSection,
        { className: styles.editor },
        React.createElement(InfoRow, {
          uppercase: true,
          disableFocus: true,
          left: "BUS BOARDING ZONE",
          right: zone.customized ? "CUSTOM" : "AUTOMATIC"
        }),
        !zone.available && React.createElement(InfoRow, {
          disableFocus: true,
          left: "Preview",
          right: "Waiting for a bus to approach"
        }),
        zone.available && React.createElement(
          React.Fragment,
          null,
          React.createElement(InfoRow, {
            disableFocus: true,
            left: "Length",
            right: `${Math.round(length)} m`
          }),
          React.createElement("div", { className: styles.sliderRow }, React.createElement(Slider, {
            value: length,
            start: 6,
            end: 200,
            valueTransformer: oneMetreSteps,
            onChange: changeLength
          })),
          React.createElement(InfoRow, {
            disableFocus: true,
            left: "Boarding",
            right: "All stopped buses inside"
          }),
          React.createElement(InfoRow, {
            disableFocus: true,
            left: "Customise",
            right: React.createElement(Segmented, {
              value: scope,
              onChange: (value) => setWholeLine(value === "line"),
              options: [
                { value: "stop", label: "This stop" },
                { value: "line", label: "Whole line", disabled: !zone.hasLine }
              ]
            })
          }),
          React.createElement(InfoRow, {
            disableFocus: true,
            left: scope === "line" ? "Line custom colour" : "Stop custom colour",
            right: React.createElement(ColorCustomizeField, {
              value: scope === "line" ? zone.routeColor : zone.stopColor,
              onChange: changeStopColor,
              className: styles.colorField
            })
          }),
          scope === "stop" && React.createElement(InfoRow, {
            disableFocus: true,
            // With a custom stop colour set, neither choice is active: picking one
            // clears the custom colour, which is the only way back from it.
            left: colourSource === "custom" ? "Colour source (custom)" : "Colour source",
            right: React.createElement(Segmented, {
              value: colourSource,
              onChange: setColourSource,
              options: [
                { value: "global", label: "Global" },
                { value: "line", label: "Line colour", disabled: !zone.hasLine }
              ]
            })
          }),
          React.createElement(InfoRow, {
            disableFocus: true,
            left: "Map editing",
            right: React.createElement(Button, {
              theme: secondaryButtonTheme,
              className: styles.resetButton,
              onSelect: toggleEditing
            }, zone.editing ? "Finish editing" : "Edit on map")
          }),
          zone.editing && React.createElement(InfoRow, {
            disableFocus: true,
            left: "Handles",
            right: "Cyan rear corners resize"
          }),
          zone.editing && React.createElement(InfoRow, {
            disableFocus: true,
            left: "Exit map editing",
            right: "Right-click or Esc"
          }),
          zone.customized && React.createElement(InfoRow, {
            disableFocus: true,
            left: "Zone override",
            right: React.createElement(Button, {
              theme: secondaryButtonTheme,
              className: styles.resetButton,
              onSelect: reset
            }, "Use automatic")
          })
        )
      );
    };

    const LinesSectionWithEditor = (props) => React.createElement(
      React.Fragment,
      null,
      React.createElement(OriginalLinesSection, props),
      React.createElement(EditorBoundary, null, React.createElement(ZoneEditor, null))
    );

    LinesSectionWithEditor[editorMarker] = true;
    return LinesSectionWithEditor;
  };

  if (!Slider || !useStepTransformer || !InfoSection || !InfoRow || !Button ||
      !ColorCustomizeField) {
    console.error("[Concurrent Bus Boarding] Required game UI components are unavailable; " +
      "the boarding-zone editor was not attached.");
    return;
  }

  moduleRegistry.extend(selectedInfoSectionsModule, "selectedInfoSectionComponents", (components) => {
    if (components[linesSectionType])
      components[linesSectionType] = withZoneEditor(components[linesSectionType]);
    return components;
  });
}
