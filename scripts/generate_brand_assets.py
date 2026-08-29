"""Generate the WinUI raster/icon assets from the PowerCulprit micro-mark.

Requires Pillow (``python -m pip install pillow``). The SVG remains the source
of truth for the title bar; this script produces crisp, consistent raster
variants for the unpackaged app, tray icon and future MSIX packaging.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1] / "src" / "PowerCulprit.Desktop" / "Assets"
BG = (11, 23, 41, 255)
TEAL = (56, 189, 248, 255)
GREEN = (74, 222, 128, 255)
YELLOW = (250, 204, 21, 255)
WHITE = (255, 247, 237, 255)


def draw_mark(size: tuple[int, int], *, transparent: bool = False) -> Image.Image:
    width, height = size
    scale = 4
    image = Image.new("RGBA", (width * scale, height * scale), (0, 0, 0, 0) if transparent else BG)
    draw = ImageDraw.Draw(image)

    if not transparent:
        radius = max(4, min(width, height) // 5) * scale
        draw.rounded_rectangle((0, 0, width * scale - 1, height * scale - 1), radius=radius, fill=BG)

    unit = min(width, height) * scale
    battery_w = unit * 0.68
    battery_h = unit * 0.48
    left = (width * scale - battery_w) / 2 - unit * 0.03
    top = (height * scale - battery_h) / 2
    right = left + battery_w
    bottom = top + battery_h
    stroke = max(2, int(unit * 0.045))

    draw.rounded_rectangle((left, top, right, bottom), radius=unit * 0.11, fill=(8, 17, 31, 255), outline=TEAL, width=stroke)
    terminal_w = unit * 0.08
    draw.rounded_rectangle((right, top + battery_h * 0.34, right + terminal_w, top + battery_h * 0.66), radius=stroke, fill=TEAL)

    points = [
        (left + battery_w * 0.10, top + battery_h * 0.76),
        (left + battery_w * 0.31, top + battery_h * 0.61),
        (left + battery_w * 0.49, top + battery_h * 0.68),
        (left + battery_w * 0.72, top + battery_h * 0.40),
        (left + battery_w * 0.90, top + battery_h * 0.23),
    ]
    draw.line(points, fill=GREEN, width=max(2, int(unit * 0.035)), joint="curve")

    bolt = [
        (left + battery_w * 0.56, top + battery_h * 0.04),
        (left + battery_w * 0.39, top + battery_h * 0.53),
        (left + battery_w * 0.53, top + battery_h * 0.53),
        (left + battery_w * 0.46, top + battery_h * 0.98),
        (left + battery_w * 0.70, top + battery_h * 0.39),
        (left + battery_w * 0.56, top + battery_h * 0.39),
    ]
    draw.polygon(bolt, fill=YELLOW, outline=WHITE)

    return image.resize((width, height), Image.Resampling.LANCZOS)


def main() -> None:
    ROOT.mkdir(parents=True, exist_ok=True)
    specs = {
        "LockScreenLogo.scale-200.png": (48, 48, True),
        "SplashScreen.scale-200.png": (1240, 600, False),
        "Square150x150Logo.scale-200.png": (300, 300, False),
        "Square44x44Logo.scale-200.png": (88, 88, False),
        "Square44x44Logo.targetsize-24_altform-unplated.png": (24, 24, True),
        "Square44x44Logo.targetsize-48_altform-lightunplated.png": (48, 48, True),
        "StoreLogo.png": (50, 50, False),
        "Wide310x150Logo.scale-200.png": (620, 300, False),
    }
    for name, (width, height, transparent) in specs.items():
        draw_mark((width, height), transparent=transparent).save(ROOT / name)

    ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    draw_mark((256, 256), transparent=False).save(ROOT / "AppIcon.ico", format="ICO", sizes=ico_sizes)


if __name__ == "__main__":
    main()
