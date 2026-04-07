export function getSaveActionVisualState(saveState) {
  switch (saveState) {
    case "loading":
      return { icon: "progress_activity", className: "is-loading" };
    case "success":
      return { icon: "check_circle", className: "is-success" };
    case "error":
      return { icon: "cancel", className: "is-error" };
    default:
      return { icon: "save", className: "" };
  }
}
