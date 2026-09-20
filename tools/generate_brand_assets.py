from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
SOURCE = ASSETS / "PaperNook.master.png"
PNG_OUTPUT = ASSETS / "PaperNook.png"
ICO_OUTPUT = ASSETS / "PaperNook.ico"


def connected_alpha(image: Image.Image) -> Image.Image:
    alpha = image.getchannel("A")
    binary = alpha.point(lambda value: 255 if value >= 24 else 0)
    seed = (binary.width // 2, binary.height // 2)
    if binary.getpixel(seed) == 0:
        raise RuntimeError("The brand mark does not cover the image center.")

    ImageDraw.floodfill(binary, seed, 128, thresh=0)
    connected = binary.point(lambda value: 255 if value == 128 else 0)
    connected = connected.filter(ImageFilter.MaxFilter(5))
    return ImageChops.multiply(alpha, connected)


def main() -> None:
    source = Image.open(SOURCE).convert("RGBA")
    source.putalpha(connected_alpha(source))
    bounds = source.getchannel("A").getbbox()
    if bounds is None:
        raise RuntimeError("The brand mark has no visible pixels.")

    mark = source.crop(bounds)
    target_extent = 840
    scale = min(target_extent / mark.width, target_extent / mark.height)
    size = (round(mark.width * scale), round(mark.height * scale))
    mark = mark.resize(size, Image.Resampling.LANCZOS)

    canvas = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
    offset = ((canvas.width - mark.width) // 2, (canvas.height - mark.height) // 2)
    canvas.alpha_composite(mark, offset)
    canvas.save(PNG_OUTPUT, optimize=True)
    canvas.save(
        ICO_OUTPUT,
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
               (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
