import heroBlurDark from "../images/hero-desktop-blur-dark.webp";
import heroBlurLight from "../images/hero-desktop-blur.webp";
import heroDark from "../images/hero-desktop-xl-dark.webp";
import heroLight from "../images/hero-desktop-xl.webp";
import { useTheme } from "../theme/mode/ThemeMode";

type HeroImageProps = {
  readonly alt: string;
  readonly className?: string;
};

/**
 * Product screenshot montage shown beside the login and signup forms. Resolves the light or dark
 * asset from the active theme, like `Logo`, so consumers never pick a variant by hand. A tiny
 * blurred placeholder sits behind the image until the full-size webp has loaded.
 */
export function HeroImage({ alt, className }: HeroImageProps) {
  const { resolvedTheme } = useTheme();
  const isDark = resolvedTheme
    ? resolvedTheme === "dark"
    : typeof document !== "undefined" && document.documentElement.classList.contains("dark");

  return (
    <div
      className={`h-auto w-full max-w-[64rem] bg-cover bg-center bg-no-repeat ${className ?? ""}`}
      style={{ backgroundImage: `url(${isDark ? heroBlurDark : heroBlurLight})`, aspectRatio: "1796/1426" }}
    >
      <img
        src={isDark ? heroDark : heroLight}
        width={1796}
        height={1426}
        alt={alt}
        fetchPriority="high"
        className="h-auto w-full"
      />
    </div>
  );
}
