import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import styles from "./zone-editor.css";

const zoneEditor$ = bindValue("ConcurrentBusBoarding", "zoneEditor", {
  visible: false,
  available: false,
  customized: false,
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
      const oneMetreSteps = useStepTransformer(1);

      React.useEffect(() => {
        setLength(zone.length ?? 26);
      }, [zone.length, zone.customized]);
      const changeLength = (value) => {
        setLength(value);
        trigger("ConcurrentBusBoarding", "setZone", 0, value);
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

  moduleRegistry.extend(selectedInfoSectionsModule, "selectedInfoSectionComponents", (components) => {
    if (components[linesSectionType])
      components[linesSectionType] = withZoneEditor(components[linesSectionType]);
    return components;
  });
}
