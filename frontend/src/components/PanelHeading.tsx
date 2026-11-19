interface PanelHeadingProps {
  id: string;
  icon: string;
  iconClassName?: string;
  kicker: string;
  title: string;
}

/** The heading of a panel; its id names the panel's region (aria-labelledby), which tests find by role. */
export function PanelHeading({ id, icon, iconClassName = "", kicker, title }: PanelHeadingProps) {
  return (
    <div className="panel-heading">
      <span className={`panel-icon ${iconClassName}`.trim()} aria-hidden="true">{icon}</span>
      <div><p className="section-kicker">{kicker}</p><h2 id={id}>{title}</h2></div>
    </div>
  );
}
