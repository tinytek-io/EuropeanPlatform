import { t } from "@lingui/core/macro";
import { useLingui } from "@lingui/react";
import { Trans } from "@lingui/react/macro";
import localeMap from "@repo/infrastructure/translations/i18n.config";
import { type Locale, translationContext } from "@repo/infrastructure/translations/TranslationContext";
import { Avatar, AvatarFallback } from "@repo/ui/components/Avatar";
import { Badge } from "@repo/ui/components/Badge";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger
} from "@repo/ui/components/DropdownMenu";
import { collapsedContext } from "@repo/ui/components/Sidebar";
import { SIDE_MENU_DEFAULT_WIDTH_REM } from "@repo/ui/utils/responsive";
import {
  CheckIcon,
  GlobeIcon,
  LogOutIcon,
  MoonIcon,
  MoonStarIcon,
  SunIcon,
  SunMoonIcon,
  ZoomInIcon
} from "lucide-react";
import { useTheme } from "next-themes";
import { use, useContext, useEffect, useState } from "react";

import { AvatarMenuTriggerContent } from "@/shared/components/AvatarMenuTriggerContent";
import { useMe } from "@/shared/hooks/useMe";

const zoomLevelStorageKey = "zoom-level";

const zoomLevelOptions = [
  { value: "0.875", label: () => t`Small` },
  { value: "1", label: () => t`Default` },
  { value: "1.125", label: () => t`Large` },
  { value: "1.25", label: () => t`Larger` }
];

function renderThemeIcon(theme: string | undefined, resolvedTheme: string | undefined) {
  if (theme === "dark") return <MoonIcon className="size-5" />;
  if (theme === "light") return <SunIcon className="size-5" />;
  if (resolvedTheme === "dark") return <MoonStarIcon className="size-5" />;
  return <SunMoonIcon className="size-5" />;
}

export function BackOfficeAvatarMenu() {
  const isCollapsed = useContext(collapsedContext);
  const { theme, setTheme, resolvedTheme } = useTheme();
  const { setLocale } = use(translationContext);
  const locales = Object.keys(localeMap) as Locale[];
  const getLocaleInfo = (locale: Locale) => localeMap[locale];
  const { i18n } = useLingui();
  const currentLocale = i18n.locale as Locale;
  const [currentZoomLevel, setCurrentZoomLevel] = useState("1");
  const [isMenuOpen, setIsMenuOpen] = useState(false);
  const { data: me } = useMe();
  const displayName = me?.displayName ?? "";
  const email = me?.email ?? "";
  const isAdmin = me?.isAdmin ?? false;
  const initials = displayName
    ? displayName
        .split(/\s+/)
        .filter(Boolean)
        .slice(0, 2)
        .map((segment) => segment.charAt(0).toUpperCase())
        .join("")
    : "PP";

  useEffect(() => {
    const saved = localStorage.getItem(zoomLevelStorageKey);
    if (saved) setCurrentZoomLevel(saved);
  }, []);

  const handleLocaleChange = (locale: Locale) => {
    if (locale !== currentLocale) {
      setLocale(locale);
    }
  };

  const handleZoomChange = (value: string) => {
    if (value === currentZoomLevel) return;
    if (value === "1") {
      localStorage.removeItem(zoomLevelStorageKey);
    } else {
      localStorage.setItem(zoomLevelStorageKey, value);
    }
    document.documentElement.style.setProperty("--zoom-level", value);
    setCurrentZoomLevel(value);
    globalThis.location.reload();
  };

  const handleLogout = () => {
    globalThis.location.href = "/.auth/logout";
  };

  const themeIcon = renderThemeIcon(theme, resolvedTheme);

  const triggerClassName = `relative flex h-[var(--control-height)] cursor-pointer items-center gap-0 overflow-visible rounded-md border-0 py-2 font-normal text-sm outline-ring hover:bg-hover-background focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 ${
    isCollapsed ? "ml-[0.5625rem] w-[var(--control-height)] justify-center" : "w-full pr-2 pl-3"
  } ${isMenuOpen ? "bg-hover-background" : ""}`;

  return (
    <div className="relative w-full px-3">
      <DropdownMenu open={isMenuOpen} onOpenChange={setIsMenuOpen}>
        <DropdownMenuTrigger className={triggerClassName} aria-label={t`User menu`}>
          <AvatarMenuTriggerContent isCollapsed={isCollapsed} initials={initials} displayName={displayName} />
        </DropdownMenuTrigger>
        <DropdownMenuContent
          align="start"
          side={isCollapsed ? "right" : "bottom"}
          className="w-auto bg-popover"
          style={{ minWidth: `${SIDE_MENU_DEFAULT_WIDTH_REM - 1.5}rem` }}
        >
          <div className="flex flex-col items-center gap-1 px-4 py-3">
            <Avatar className="size-16">
              <AvatarFallback className="text-xl">{initials}</AvatarFallback>
            </Avatar>
            <span className="font-medium">{displayName || <Trans>Back Office</Trans>}</span>
            {email && <span className="text-sm text-muted-foreground">{email}</span>}
            {isAdmin && (
              <Badge variant="outline" className="mt-1">
                <Trans>Admin</Trans>
              </Badge>
            )}
          </div>

          <DropdownMenuSeparator />

          <DropdownMenuSub>
            <DropdownMenuSubTrigger aria-label={t`Change theme`}>
              {themeIcon}
              <Trans>Theme</Trans>
            </DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              <DropdownMenuItem onClick={() => setTheme("system")}>
                {resolvedTheme === "dark" ? <MoonStarIcon className="size-5" /> : <SunMoonIcon className="size-5" />}
                <Trans>System</Trans>
                {theme === "system" && <CheckIcon className="ml-auto size-4" />}
              </DropdownMenuItem>
              <DropdownMenuItem onClick={() => setTheme("light")}>
                <SunIcon className="size-5" />
                <Trans>Light</Trans>
                {theme === "light" && <CheckIcon className="ml-auto size-4" />}
              </DropdownMenuItem>
              <DropdownMenuItem onClick={() => setTheme("dark")}>
                <MoonIcon className="size-5" />
                <Trans>Dark</Trans>
                {theme === "dark" && <CheckIcon className="ml-auto size-4" />}
              </DropdownMenuItem>
            </DropdownMenuSubContent>
          </DropdownMenuSub>

          <DropdownMenuSub>
            <DropdownMenuSubTrigger aria-label={t`Change language`}>
              <GlobeIcon className="size-5" />
              <Trans>Language</Trans>
            </DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              {locales.map((locale) => (
                <DropdownMenuItem key={locale} onClick={() => handleLocaleChange(locale)}>
                  <span>{getLocaleInfo(locale).label}</span>
                  {locale === currentLocale && <CheckIcon className="ml-auto size-4" />}
                </DropdownMenuItem>
              ))}
            </DropdownMenuSubContent>
          </DropdownMenuSub>

          <DropdownMenuSub>
            <DropdownMenuSubTrigger aria-label={t`Change zoom level`}>
              <ZoomInIcon className="size-5" />
              <Trans>Zoom</Trans>
            </DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              {zoomLevelOptions.map((zoom) => (
                <DropdownMenuItem key={zoom.value} onClick={() => handleZoomChange(zoom.value)}>
                  <span>{zoom.label()}</span>
                  {zoom.value === currentZoomLevel && <CheckIcon className="ml-auto size-4" />}
                </DropdownMenuItem>
              ))}
            </DropdownMenuSubContent>
          </DropdownMenuSub>

          <DropdownMenuSeparator />

          <DropdownMenuItem onClick={handleLogout} aria-label={t`Log out`}>
            <LogOutIcon className="size-5" />
            <Trans>Log out</Trans>
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );
}
