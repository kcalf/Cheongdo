export function Loader({ label }: { label: string }) {
  return (
    <div className="loader" role="status" aria-live="polite">
      <div className="ring"><img src="/mascot-small.png" alt="" /></div>
      <span>{label}</span>
    </div>
  );
}
