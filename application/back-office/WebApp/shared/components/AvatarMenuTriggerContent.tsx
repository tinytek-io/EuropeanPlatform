import { Trans } from "@lingui/react/macro";
import { Avatar, AvatarFallback } from "@repo/ui/components/Avatar";
import { ChevronsUpDownIcon } from "lucide-react";

interface AvatarMenuTriggerContentProps {
  isCollapsed: boolean;
  initials: string;
  displayName: string;
}

export function AvatarMenuTriggerContent({
  isCollapsed,
  initials,
  displayName
}: Readonly<AvatarMenuTriggerContentProps>) {
  return (
    <>
      <Avatar>
        <AvatarFallback>{initials}</AvatarFallback>
      </Avatar>
      {!isCollapsed && (
        <>
          <div className="ml-3 flex-1 overflow-hidden text-left font-medium text-ellipsis whitespace-nowrap text-foreground">
            {displayName || <Trans>Back Office</Trans>}
          </div>
          <ChevronsUpDownIcon className="ml-2 size-3.5 shrink-0 text-foreground opacity-70" />
        </>
      )}
    </>
  );
}
