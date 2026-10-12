import { t } from "@lingui/core/macro";
import { useLingui } from "@lingui/react";
import { Trans } from "@lingui/react/macro";
import localeMap from "@repo/infrastructure/translations/i18n.config";
import { type Locale, translationContext } from "@repo/infrastructure/translations/TranslationContext";
import { Button } from "@repo/ui/components/Button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger
} from "@repo/ui/components/DropdownMenu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@repo/ui/components/Tooltip";
import { CheckIcon, GlobeIcon, MoonIcon, MoonStarIcon, SunIcon, SunMoonIcon } from "lucide-react";
import { useTheme } from "next-themes";
import { use } from "react";

export function LoginFooterControls() {
  return (
    <div className="flex gap-4 rounded-md bg-card p-2 shadow-md">
      <ThemeDropdown />
      <LocaleDropdown />
    </div>
  );
}

function renderThemeIcon(theme: string | undefined, resolvedTheme: string | undefined) {
  if (theme === "dark") return <MoonIcon className="size-5" />;
  if (theme === "light") return <SunIcon className="size-5" />;
  if (resolvedTheme === "dark") return <MoonStarIcon className="size-5" />;
  return <SunMoonIcon className="size-5" />;
}

function ThemeDropdown() {
  const { theme, setTheme, resolvedTheme } = useTheme();

  const themeIcon = renderThemeIcon(theme, resolvedTheme);

  return (
    <DropdownMenu trackingTitle="Theme menu">
      <Tooltip>
        <TooltipTrigger
          render={
            <DropdownMenuTrigger
              render={
                <Button variant="ghost" size="icon" aria-label={t`Change theme`}>
                  {themeIcon}
                </Button>
              }
            />
          }
        />
        <TooltipContent>{t`Change theme`}</TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="start">
        <DropdownMenuItem trackingLabel="System" onClick={() => setTheme("system")}>
          {resolvedTheme === "dark" ? <MoonStarIcon className="size-5" /> : <SunMoonIcon className="size-5" />}
          <Trans>System</Trans>
          {theme === "system" && <CheckIcon className="ml-auto size-4" />}
        </DropdownMenuItem>
        <DropdownMenuItem trackingLabel="Light" onClick={() => setTheme("light")}>
          <SunIcon className="size-5" />
          <Trans>Light</Trans>
          {theme === "light" && <CheckIcon className="ml-auto size-4" />}
        </DropdownMenuItem>
        <DropdownMenuItem trackingLabel="Dark" onClick={() => setTheme("dark")}>
          <MoonIcon className="size-5" />
          <Trans>Dark</Trans>
          {theme === "dark" && <CheckIcon className="ml-auto size-4" />}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function LocaleDropdown() {
  const { setLocale } = use(translationContext);
  const { i18n } = useLingui();
  const currentLocale = i18n.locale as Locale;
  const locales = Object.keys(localeMap) as Locale[];
  const getLocaleInfo = (locale: Locale) => localeMap[locale];

  const handleLocaleChange = (locale: Locale) => {
    if (locale !== currentLocale) {
      setLocale(locale);
    }
  };

  return (
    <DropdownMenu trackingTitle="Language menu">
      <Tooltip>
        <TooltipTrigger
          render={
            <DropdownMenuTrigger
              render={
                <Button variant="ghost" size="icon" aria-label={t`Change language`}>
                  <GlobeIcon className="size-5" />
                </Button>
              }
            />
          }
        />
        <TooltipContent>{t`Change language`}</TooltipContent>
      </Tooltip>
      <DropdownMenuContent>
        {locales.map((locale) => (
          <DropdownMenuItem
            key={locale}
            trackingLabel={getLocaleInfo(locale).label}
            onClick={() => handleLocaleChange(locale)}
          >
            <span>{getLocaleInfo(locale).label}</span>
            {locale === currentLocale && <CheckIcon className="ml-auto size-4" />}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
