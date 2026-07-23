import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import styles from "./zone-editor.css";

const zoneEditor$ = bindValue("ConcurrentBusBoarding", "zoneEditor", {
  visible: false,
  available: false,
  customized: false,
  editing: false,
  offset: 0,
  length: 26,
  color: "#2f8fe8"
});
const selectedInfoSectionsModule = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const linesSectionType = "Game.UI.InGame.LinesSection";
const sliderModule = "game-ui/common/input/slider/slider.tsx";
const infoSectionModule = "game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx";
const infoRowModule = "game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx";
const buttonModule = "game-ui/common/input/button/button.tsx";
const secondaryButtonThemeModule = "game-ui/common/input/button/themes/paradox-secondary-button.module.scss";
const editorMarker = Symbol.for("ConcurrentBusBoarding.ZoneEditor");

export default function register(moduleRegistry) {
  console.log("[Concurrent Bus Boarding] UI module registered.");
  const Slider = moduleRegistry.get(sliderModule, "Slider");
  const useStepTransformer = moduleRegistry.get(sliderModule, "useStepTransformer");
  const InfoSection = moduleRegistry.get(infoSectionModule, "InfoSection");
  const InfoRow = moduleRegistry.get(infoRowModule, "InfoRow");
  const Button = moduleRegistry.get(buttonModule, "Button");
  const secondaryButtonTheme = moduleRegistry.get(secondaryButtonThemeModule, "classes");

  const withZoneEditor = (OriginalLinesSection) => {
    if (OriginalLinesSection[editorMarker])
      return OriginalLinesSection;

    const LinesSectionWithEditor = (props) => {
      const zone = useValue(zoneEditor$);
      const [length, setLength] = React.useState(zone.length ?? 26);
      const [color, setColor] = React.useState(parseColor(zone.color));
      const oneMetreSteps = useStepTransformer(1);

      React.useEffect(() => {
        setLength(zone.length ?? 26);
        setColor(parseColor(zone.color));
      }, [zone.length, zone.customized, zone.color]);
      const changeLength = (value) => {
        setLength(value);
        trigger("ConcurrentBusBoarding", "setZone", 0, value);
      };
      const changeColor = (event) => {
        const next = [0, 0, 0, 1];
        const raw = event.target.value;
        if (raw) {
          const hex = raw.replace('#', '');
          const normalized = hex.length === 3 ? hex.split('').map((part) => part + part).join('') : hex;
          if (normalized.length === 6) {
            next[0] = parseInt(normalized.slice(0, 2), 16) / 255;
            next[1] = parseInt(normalized.slice(2, 4), 16) / 255;
            next[2] = parseInt(normalized.slice(4, 6), 16) / 255;
          }
        }
        setColor(next);
        trigger("ConcurrentBusBoarding", "setZoneColor", next[0], next[1], next[2], 0.28);
      };
      const reset = () => trigger("ConcurrentBusBoarding", "resetZone");
      const toggleEditing = () => trigger("ConcurrentBusBoarding", "toggleZoneEditing");

      return React.createElement(
        React.Fragment,
        null,
        React.createElement(OriginalLinesSection, props),
        zone.visible && React.createElement(
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
              left: "Overlay color",
              right: React.createElement("input", {
                className: styles.colorInput,
                type: "text",
                value: color && color.length >= 3 ? rgbToHex(color[0], color[1], color[2]) : "#2f8fe8",
                onChange: changeColor
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
            React.createElement(InfoRow, {
              disableFocus: true,
              left: "Exit map editing",
              right: "Right-click or Esc"
            }),
            zone.editing && React.createElement(InfoRow, {
              disableFocus: true,
              left: "Handles",
              right: "Cyan rear corners resize"
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
        )
      );
    };

    LinesSectionWithEditor[editorMarker] = true;
    return LinesSectionWithEditor;
  };

  const parseColor = (value) => {
    if (Array.isArray(value))
      return value;
    if (typeof value === "string" && value.startsWith("#")) {
      const hex = value.replace("#", "");
      if (hex.length === 6) {
        return [
          parseInt(hex.slice(0, 2), 16) / 255,
          parseInt(hex.slice(2, 4), 16) / 255,
          parseInt(hex.slice(4, 6), 16) / 255
        ];
      }
    }
    return [0.15, 0.55, 0.95];
  };
  const rgbToHex = (r, g, b) => {
    const toHex = (value) => Math.max(0, Math.min(255, Math.round(value * 255))).toString(16).padStart(2, "0");
    return `#${toHex(r)}${toHex(g)}${toHex(b)}`;
  };

  moduleRegistry.extend(selectedInfoSectionsModule, "selectedInfoSectionComponents", (components) => {
    if (components[linesSectionType])
      components[linesSectionType] = withZoneEditor(components[linesSectionType]);
    return components;
  });
}
