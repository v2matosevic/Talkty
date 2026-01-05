"""
Generate Microsoft Store assets from the source icon.
Requires: Pillow (pip install Pillow)

Creates all required icon sizes for MSIX packaging:
- Square44x44Logo (with scale variants)
- Square71x71Logo (with scale variants)
- Square150x150Logo (with scale variants)
- Square310x310Logo (with scale variants)
- Wide310x150Logo (with scale variants)
- StoreLogo (with scale variants)
- SplashScreen (620x300)
"""

from pathlib import Path
import sys

try:
    from PIL import Image, ImageDraw
except ImportError:
    print("Error: Pillow not installed. Run: pip install Pillow")
    sys.exit(1)

# Source icon - try ICO first (new icon), then PNG
SCRIPT_DIR = Path(__file__).parent
PROJECT_ROOT = SCRIPT_DIR.parent
SOURCE_ICON = PROJECT_ROOT / "Talkty.App" / "Resources" / "tray.ico"
SOURCE_PNG = PROJECT_ROOT / "icon_preview.png"
ASSETS_DIR = PROJECT_ROOT / "Talkty.App" / "Assets"

# Asset specifications: (base_name, [(scale, size), ...])
SQUARE_ASSETS = {
    "Square44x44Logo": [(100, 44), (125, 55), (150, 66), (200, 88), (400, 176)],
    "Square71x71Logo": [(100, 71), (125, 89), (150, 107), (200, 142), (400, 284)],
    "Square150x150Logo": [(100, 150), (125, 188), (150, 225), (200, 300), (400, 600)],
    "Square310x310Logo": [(100, 310), (125, 388), (150, 465), (200, 620), (400, 1240)],
    "StoreLogo": [(100, 50), (125, 63), (150, 75), (200, 100), (400, 200)],
}

WIDE_ASSETS = {
    "Wide310x150Logo": [(100, (310, 150)), (125, (388, 188)), (150, (465, 225)), (200, (620, 300)), (400, (1240, 600))],
}

SPLASH_SCREEN = {
    "SplashScreen": [(100, (620, 300)), (125, (775, 375)), (150, (930, 450)), (200, (1240, 600))],
}


def create_square_icon(source: Image.Image, size: int) -> Image.Image:
    """Resize source to square with padding if needed."""
    # Resize maintaining aspect ratio
    img = source.copy()
    img.thumbnail((size, size), Image.Resampling.LANCZOS)

    # Create square canvas with transparent background
    result = Image.new('RGBA', (size, size), (0, 0, 0, 0))

    # Center the image
    x = (size - img.width) // 2
    y = (size - img.height) // 2
    result.paste(img, (x, y), img if img.mode == 'RGBA' else None)

    return result


def create_wide_icon(source: Image.Image, width: int, height: int) -> Image.Image:
    """Create wide banner with centered icon."""
    # Create canvas with app background color
    result = Image.new('RGBA', (width, height), (18, 18, 20, 255))  # #121214

    # Scale icon to fit height with padding
    icon_size = int(height * 0.6)
    icon = source.copy()
    icon.thumbnail((icon_size, icon_size), Image.Resampling.LANCZOS)

    # Center horizontally, vertically aligned
    x = (width - icon.width) // 2
    y = (height - icon.height) // 2
    result.paste(icon, (x, y), icon if icon.mode == 'RGBA' else None)

    return result


def create_splash_screen(source: Image.Image, width: int, height: int) -> Image.Image:
    """Create splash screen with centered icon."""
    # Create canvas with app background color
    result = Image.new('RGBA', (width, height), (18, 18, 20, 255))  # #121214

    # Scale icon to reasonable size
    icon_size = min(int(height * 0.5), int(width * 0.3))
    icon = source.copy()
    icon.thumbnail((icon_size, icon_size), Image.Resampling.LANCZOS)

    # Center the icon
    x = (width - icon.width) // 2
    y = (height - icon.height) // 2
    result.paste(icon, (x, y), icon if icon.mode == 'RGBA' else None)

    return result


def main():
    print("Generating Microsoft Store assets...")

    # Create assets directory
    ASSETS_DIR.mkdir(parents=True, exist_ok=True)

    # Try ICO first (new icon), then PNG
    if SOURCE_ICON.exists():
        print(f"Using ICO source: {SOURCE_ICON}")
        source = Image.open(SOURCE_ICON)

        # ICO files can have multiple sizes - get the largest
        if hasattr(source, 'n_frames') and source.n_frames > 1:
            sizes = []
            for i in range(source.n_frames):
                source.seek(i)
                sizes.append((source.width * source.height, i, source.copy()))
            sizes.sort(reverse=True)
            source = sizes[0][2]
    elif SOURCE_PNG.exists():
        print(f"Using PNG source: {SOURCE_PNG}")
        source = Image.open(SOURCE_PNG)
    else:
        print(f"Error: No source icon found!")
        print(f"  Tried: {SOURCE_ICON}")
        print(f"  Tried: {SOURCE_PNG}")
        sys.exit(1)

    # Convert to RGBA
    source = source.convert('RGBA')
    print(f"Source icon: {source.width}x{source.height}")

    generated = 0

    # Generate square assets
    for base_name, scales in SQUARE_ASSETS.items():
        for scale, size in scales:
            if scale == 100:
                filename = f"{base_name}.png"
            else:
                filename = f"{base_name}.scale-{scale}.png"

            output_path = ASSETS_DIR / filename
            icon = create_square_icon(source, size)
            icon.save(output_path, "PNG")
            print(f"  Created: {filename} ({size}x{size})")
            generated += 1

    # Generate wide assets
    for base_name, scales in WIDE_ASSETS.items():
        for scale, (width, height) in scales:
            if scale == 100:
                filename = f"{base_name}.png"
            else:
                filename = f"{base_name}.scale-{scale}.png"

            output_path = ASSETS_DIR / filename
            icon = create_wide_icon(source, width, height)
            icon.save(output_path, "PNG")
            print(f"  Created: {filename} ({width}x{height})")
            generated += 1

    # Generate splash screen
    for base_name, scales in SPLASH_SCREEN.items():
        for scale, (width, height) in scales:
            if scale == 100:
                filename = f"{base_name}.png"
            else:
                filename = f"{base_name}.scale-{scale}.png"

            output_path = ASSETS_DIR / filename
            splash = create_splash_screen(source, width, height)
            splash.save(output_path, "PNG")
            print(f"  Created: {filename} ({width}x{height})")
            generated += 1

    print(f"\nGenerated {generated} assets in {ASSETS_DIR}")
    print("\nDone!")


if __name__ == "__main__":
    main()
