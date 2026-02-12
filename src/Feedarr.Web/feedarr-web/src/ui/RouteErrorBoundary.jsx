import React from "react";
import { useNavigate, useRouteError } from "react-router-dom";

function resolveErrorMessage(error) {
  if (!error) return "Une erreur inattendue est survenue.";
  if (typeof error === "string") return error;
  if (typeof error.message === "string" && error.message.trim()) return error.message;
  if (typeof error.statusText === "string" && error.statusText.trim()) return error.statusText;
  return "Une erreur inattendue est survenue.";
}

export default function RouteErrorBoundary() {
  const error = useRouteError();
  const navigate = useNavigate();
  const message = resolveErrorMessage(error);

  return (
    <div className="page">
      <div className="pagehead">
        <div>
          <h1>Erreur d'affichage</h1>
          <div className="muted">La page n'a pas pu etre chargee.</div>
        </div>
      </div>

      <div className="errorbox">
        <div className="errorbox__title">Détails</div>
        <div className="muted">{message}</div>
      </div>

      <div className="formactions" style={{ marginTop: 12 }}>
        <button className="btn" type="button" onClick={() => window.location.reload()}>
          Recharger
        </button>
        <button className="btn" type="button" onClick={() => navigate("/library", { replace: true })}>
          Retour à la bibliothèque
        </button>
      </div>
    </div>
  );
}
