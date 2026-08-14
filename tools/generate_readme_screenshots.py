from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ICON_PATH = ROOT / "assets" / "project-center.png"
OUTPUT_DIR = ROOT / "docs" / "images"
WIDTH, HEIGHT = 1440, 900


def load_font(size, bold=False):
    candidates = [
        Path(r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc"),
        Path(r"C:\Windows\Fonts\seguisb.ttf" if bold else r"C:\Windows\Fonts\segoeui.ttf"),
    ]
    for path in candidates:
        if path.exists():
            return ImageFont.truetype(str(path), size)
    return ImageFont.load_default()


FONT_14 = load_font(14)
FONT_16 = load_font(16)
FONT_18 = load_font(18)
FONT_18_BOLD = load_font(18, True)
FONT_22 = load_font(22)
FONT_24_BOLD = load_font(24, True)
FONT_28_BOLD = load_font(28, True)


def rounded(draw, box, radius, fill, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def text(draw, xy, value, font, fill="#202123"):
    draw.text(xy, value, font=font, fill=fill)


def tab(draw, x, label, count, selected=False):
    box = (x, 140, x + 168, 200)
    rounded(draw, box, 22, "#2F9CF4" if selected else "#FFFFFF", "#DCDCDC", 1)
    color = "#FFFFFF" if selected else "#202123"
    text(draw, (x + 22, 157), label, FONT_18_BOLD if selected else FONT_18, color)
    text(draw, (x + 128, 157), str(count), FONT_18_BOLD if selected else FONT_18, color)


def action_button(draw, x, y, label, primary=False):
    width = 96 if primary else 108
    rounded(
        draw,
        (x, y, x + width, y + 52),
        14,
        "#202123" if primary else "#FFFFFF",
        "#D8D8D8",
        1,
    )
    label_width = draw.textbbox((0, 0), label, font=FONT_18)[2]
    text(draw, (x + (width - label_width) / 2, y + 14), label, FONT_18, "#FFFFFF" if primary else "#202123")


def task_row(draw, top, project, host, age, title_value, preview, waiting):
    text(draw, (54, top + 20), project, FONT_16, "#5D6470")
    project_width = draw.textbbox((0, 0), project, font=FONT_16)[2]
    text(draw, (64 + project_width, top + 20), f"·  {host}  ·  {age}", FONT_16, "#8A9099")
    text(draw, (54, top + 52), title_value, FONT_24_BOLD)
    text(draw, (94, top + 86), preview, FONT_16, "#707780")
    action_button(draw, 1160, top + 42, "打开", True)
    if waiting:
        action_button(draw, 1270, top + 42, "已处理")
    draw.line((46, top + 126, 1394, top + 126), fill="#E1E1E1", width=1)


def generate(filename, selected, counts, rows, footer_status):
    image = Image.new("RGB", (WIDTH, HEIGHT), "#FFFFFF")
    draw = ImageDraw.Draw(image)

    draw.rectangle((0, 0, WIDTH, 112), fill="#FFFFFF")
    draw.line((0, 112, WIDTH, 112), fill="#DDDDDD", width=1)
    icon = Image.open(ICON_PATH).convert("RGBA").resize((64, 64), Image.Resampling.LANCZOS)
    image.paste(icon, (30, 24), icon)
    text(draw, (112, 42), "Codex Deck", FONT_28_BOLD)
    text(draw, (294, 49), "· 任务状态", FONT_18, "#5D6470")

    rounded(draw, (1160, 28, 1270, 84), 14, "#FFFFFF", "#D8D8D8")
    text(draw, (1188, 45), "刷新", FONT_18)
    rounded(draw, (1290, 28, 1348, 84), 14, "#F7F7F7", "#D8D8D8")
    text(draw, (1310, 43), "—", FONT_22)
    rounded(draw, (1360, 28, 1418, 84), 14, "#F7F7F7", "#D8D8D8")
    text(draw, (1379, 43), "×", FONT_22)

    draw.rectangle((0, 113, WIDTH, 220), fill="#F7F7F8")
    draw.line((0, 220, WIDTH, 220), fill="#DDDDDD", width=1)
    tab(draw, 40, "待我处理", counts[0], selected == "waiting")
    tab(draw, 220, "进行中", counts[1], selected == "running")
    tab(draw, 400, "最近完成", counts[2], selected == "completed")

    for index, row in enumerate(rows):
        task_row(draw, 236 + index * 142, *row, waiting=selected == "waiting")

    draw.rectangle((0, 842, WIDTH, HEIGHT), fill="#FFFFFF")
    draw.line((0, 842, WIDTH, 842), fill="#DDDDDD", width=1)
    text(draw, (38, 861), "已扫描 24 个任务（含历史记录）", FONT_16, "#5D6470")
    status_width = draw.textbbox((0, 0), footer_status, font=FONT_16)[2]
    text(draw, (WIDTH - status_width - 38, 861), footer_status, FONT_16, "#5D6470")

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    image.save(OUTPUT_DIR / filename, optimize=True)


generate(
    "codex-deck-waiting.png",
    "waiting",
    (3, 2, 18),
    [
        ("web-console", "远程 · work", "刚刚", "检查部署结果并确认", "任务已完成，等待你查看结果"),
        ("api-service", "本机", "2 分钟前", "确认数据库迁移方案", "Codex 请求你选择回滚策略"),
        ("docs-site", "远程 · studio", "8 分钟前", "审核发布说明", "文档已经生成，等待最终确认"),
    ],
    "本机与远程状态已同步",
)

generate(
    "codex-deck-running.png",
    "running",
    (1, 3, 18),
    [
        ("backend-api", "远程 · work", "刚刚", "修复登录状态同步", "正在运行自动化测试并检查失败用例"),
        ("desktop-client", "本机", "1 分钟前", "优化窗口切换速度", "正在分析性能日志和窗口焦点事件"),
        ("docs-site", "远程 · studio", "4 分钟前", "更新开源文档", "正在生成示例截图并检查文档链接"),
    ],
    "3 个任务正在运行",
)

print(OUTPUT_DIR / "codex-deck-waiting.png")
print(OUTPUT_DIR / "codex-deck-running.png")
