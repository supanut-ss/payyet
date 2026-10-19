"""Build the Pay Yet rich menu image and (optionally) register it with LINE.

Usage:
  python richmenu.py build                      # writes richmenu.png + richmenu.json
  python richmenu.py deploy                     # build + create + upload image + set as default
Env (deploy only): LINE_CHANNEL_ACCESS_TOKEN, LIFF_ID
"""
import json, os, sys, urllib.request
from PIL import Image, ImageDraw, ImageFont

W, H = 2500, 1686
FONT = r"C:\Windows\Fonts\tahomabd.ttf"  # supports Thai
LIFF_BASE = "https://liff.line.me/{}".format(os.environ.get("LIFF_ID", "YOUR_LIFF_ID"))
HELP_URL = "https://drivetodev.online/payyet/"   # landing page with every command and feature

# (label, icon, bg color, x, y, w, h, action)
TOP = 923  # matches the row split in design.webp
BOT = H - TOP
BUTTONS = [
    ("เก็บเงิน", "💰", "#06C755", 0, 0, W // 2, TOP, {"type": "uri", "label": "เก็บเงิน", "uri": LIFF_BASE + "?mode=new"}),
    ("ยอดค้าง", "📋", "#1E88E5", W // 2, 0, W // 2, TOP, {"type": "uri", "label": "ยอดค้าง", "uri": LIFF_BASE + "?mode=owe"}),
    ("เปิดแอป", "📱", "#8E24AA", 0, TOP, 833, BOT, {"type": "uri", "label": "เปิดแอป", "uri": LIFF_BASE + "?mode=home"}),
    ("พร้อมเพย์", "🏦", "#F57C00", 833, TOP, 834, BOT, {"type": "uri", "label": "พร้อมเพย์", "uri": LIFF_BASE + "?mode=promptpay"}),
    ("วิธีใช้", "❓", "#546E7A", 1667, TOP, 833, BOT, {"type": "uri", "label": "วิธีใช้", "uri": HELP_URL}),
]


def build():
    if os.path.exists("design.webp"):  # custom artwork: just fit it to the LINE size
        img = Image.open("design.webp").convert("RGB").resize((W, H), Image.LANCZOS)
        return finish(img)
    img = Image.new("RGB", (W, H), "white")
    d = ImageDraw.Draw(img)
    for label, _, color, x, y, w, h, _a in BUTTONS:
        d.rectangle([x + 6, y + 6, x + w - 6, y + h - 6], fill=color)
        big = h > 800
        font = ImageFont.truetype(FONT, 190 if big else 100)
        box = d.textbbox((0, 0), label, font=font)
        d.text((x + (w - (box[2] - box[0])) / 2 - box[0], y + (h - (box[3] - box[1])) / 2 - box[1]), label, font=font, fill="white")
    return finish(img)


def finish(img):
    global IMAGE, MIME
    img.save("richmenu.png", optimize=True)
    IMAGE, MIME = "richmenu.png", "image/png"
    if os.path.getsize(IMAGE) > 1_000_000:  # LINE limit: 1 MB
        img.save("richmenu.jpg", quality=90)
        IMAGE, MIME = "richmenu.jpg", "image/jpeg"
    menu = {
        "size": {"width": W, "height": H},
        "selected": True,
        "name": "PayYet main menu",
        "chatBarText": "เมนู",
        "areas": [{"bounds": {"x": x, "y": y, "width": w, "height": h}, "action": a} for _, _, _, x, y, w, h, a in BUTTONS],
    }
    json.dump(menu, open("richmenu.json", "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    return menu


def call(url, token, data, ctype):
    req = urllib.request.Request(url, data=data, method="POST", headers={"Authorization": f"Bearer {token}", "Content-Type": ctype})
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read() or b"{}")


def deploy(menu):
    token = os.environ["LINE_CHANNEL_ACCESS_TOKEN"]
    rid = call("https://api.line.me/v2/bot/richmenu", token, json.dumps(menu).encode(), "application/json")["richMenuId"]
    call(f"https://api-data.line.me/v2/bot/richmenu/{rid}/content", token, open(IMAGE, "rb").read(), MIME)
    call(f"https://api.line.me/v2/bot/user/all/richmenu/{rid}", token, b"", "application/json")
    print("Rich menu live:", rid)


if __name__ == "__main__":
    m = build()
    if sys.argv[1:] == ["deploy"]:
        deploy(m)
