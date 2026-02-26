import React from "react";
import { tr } from "../../app/uiText.js";

export default function Step1Intro() {
  return (
    <div className="setup-step">
      <h2>{tr("Bienvenue", "Welcome")}</h2>
      <p>{tr("Configuration rapide en 7 etapes.", "Quick setup in 7 steps.")}</p>
      <p className="muted">
        {tr(
          "Resume : langue -> metadata -> fournisseurs RSS -> indexeurs -> applications.",
          "Summary: language -> metadata -> RSS providers -> indexers -> applications."
        )}
      </p>
    </div>
  );
}
