export default function SubbarItem({ icon, label, active, onClick, disabled }) {
  return (
    <div
      className={`subbar-item ${active ? "is-active" : ""} ${disabled ? "is-disabled" : ""}`}
      onClick={disabled ? undefined : onClick}
    >
      <div className="subbar-item__icon">{icon}</div>
      <div className="subbar-item__label">{label}</div>
    </div>
  );
}
