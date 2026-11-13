import React from "react";
import { Link } from "react-router-dom";
import { Rss } from "lucide-react";
import AppIcon from "../../../ui/AppIcon.jsx";

export default function LibraryWelcome() {
  return <section className="settings-card library-welcome" aria-labelledby="library-welcome-title">
    <div className="library-welcome__content">
      <h2 id="library-welcome-title">Bienvenue dans Feedarr</h2>
      <p className="muted">Aucune source n’est configurée. Ajoutez une source ou restaurez votre configuration pour remplir la bibliothèque.</p>
      <div className="library-welcome__actions">
        <Link className="library-welcome__tile library-welcome__tile--source" to="/indexers">
          <Rss aria-hidden="true" />
          <span>Ajouter une source</span>
        </Link>
        <Link className="library-welcome__tile" to="/settings/backup">
          <AppIcon name="storage" aria-hidden="true" />
          <span>Restaurer une sauvegarde</span>
        </Link>
      </div>
    </div>
  </section>;
}
