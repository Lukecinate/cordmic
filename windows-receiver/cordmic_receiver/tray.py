"""Optional system-tray icon (pystray + Pillow). Falls back to console if unavailable."""
from __future__ import annotations

import logging
import threading

log = logging.getLogger("cordmic")


def available() -> bool:
    try:
        import pystray  # noqa: F401
        from PIL import Image  # noqa: F401
        return True
    except Exception:
        return False


def _icon_image(color):
    from PIL import Image, ImageDraw
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((22, 6, 42, 38), radius=10, fill=color)
    d.arc((14, 18, 50, 48), start=0, end=180, fill=color, width=4)
    d.line((32, 48, 32, 58), fill=color, width=4)
    d.line((22, 58, 42, 58), fill=color, width=4)
    return img


def run_tray(receiver, stop_event: threading.Event):
    import pystray

    def status_text(_item=None):
        return receiver.snapshot().line()

    def on_quit(icon, _item):
        stop_event.set()
        icon.stop()

    icon = pystray.Icon("CordMic", _icon_image((150, 150, 150, 255)), "CordMic: menunggu HP...",
                        menu=pystray.Menu(pystray.MenuItem(lambda i: status_text()[:120], None, enabled=False),
                                          pystray.MenuItem("Keluar", on_quit)))

    def on_peer(peer):
        try:
            icon.icon = _icon_image((40, 180, 90, 255) if peer else (150, 150, 150, 255))
            icon.title = f"CordMic: {peer[0]}" if peer else "CordMic: menunggu HP..."
        except Exception:
            pass

    receiver.on_peer_change = on_peer
    icon.run()  # blocks until on_quit
