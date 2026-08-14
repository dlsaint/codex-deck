from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
PNG_PATH = ASSETS / "project-center.png"
ICO_PATH = ASSETS / "project-center.ico"
SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]


def task_center_frame(size):
    scale = 4
    canvas_size = size * scale
    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    margin = round(canvas_size * .07)
    draw.rounded_rectangle(
        (margin, margin, canvas_size - margin, canvas_size - margin),
        radius=round(canvas_size * .22),
        fill="#202123",
    )

    panel = (
        round(canvas_size * .20), round(canvas_size * .19),
        round(canvas_size * .80), round(canvas_size * .81),
    )
    draw.rounded_rectangle(panel, radius=round(canvas_size * .075), fill="#FFFFFF")

    colors = ("#2F9CF4", "#F3A83B", "#36B37E")
    row_y = (.34, .50, .66)
    for color, y_ratio in zip(colors, row_y):
        y = round(canvas_size * y_ratio)
        radius = max(2, round(canvas_size * .035))
        x = round(canvas_size * .31)
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=color)
        line_left = round(canvas_size * .40)
        line_right = round(canvas_size * .69)
        line_width = max(2, round(canvas_size * .035))
        draw.line((line_left, y, line_right, y), fill="#5C6470", width=line_width)

    check_width = max(2, round(canvas_size * .028))
    draw.line(
        (
            round(canvas_size * .57), round(canvas_size * .72),
            round(canvas_size * .63), round(canvas_size * .77),
            round(canvas_size * .73), round(canvas_size * .66),
        ),
        fill="#36B37E",
        width=check_width,
        joint="curve",
    )
    return image.resize((size, size), Image.Resampling.LANCZOS)


ASSETS.mkdir(exist_ok=True)
frames = [task_center_frame(width) for width, _ in SIZES]
frames[-1].save(PNG_PATH)
frames[-1].save(ICO_PATH, append_images=frames[:-1], format="ICO", sizes=SIZES)

print(PNG_PATH)
print(ICO_PATH)
