"""Every IGamePlugin member must be forwarded by SafePluginProxy.

The launcher never holds a plugin directly -- it holds a SafePluginProxy. A
member the proxy does not forward resolves to the interface's own default
instead, so the plugin's answer never reaches the launcher. Nothing throws,
nothing logs: the feature is simply absent from the game page.

That is exactly how 27 members went missing in 3.0.0, so it is a gate now.

    python Tools/lint_proxy_complete.py
"""
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
IFACE = os.path.join(ROOT, "Core", "IGamePlugin.cs")
PROXY = os.path.join(ROOT, "Core", "Plugins", "SafePluginProxy.cs")

# Declared on the interface but deliberately answered by the proxy itself.
# Each one needs a reason, because "the proxy decides" is the exception.
SELF_ANSWERED = {
    "GameId":       "captured at construction, so a quarantined plugin still has a name",
    "DisplayName":  "same",
    "IsRunning":    "a quarantined plugin is never running",
    "VideoPreviewUrl": "the launcher refuses to load media from a plugin-chosen address",
    "ScreenshotUrls":  "same",
}


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return "\n".join(l for l in text.split("\n") if not l.strip().startswith("//"))


def interface_members(src):
    """Member names declared directly on IGamePlugin, not on nested types.

    Depth 1 is the interface body itself, so that -- and only that -- is where
    a member counts. Anything deeper belongs to a nested record or a method
    body. Getting this wrong is not harmless: a parser that finds nothing
    reports a clean run forever, which is how this gate first shipped useless.
    """
    body = src.split("public interface IGamePlugin", 1)[1]
    names, depth = [], 0
    for line in body.split("\n"):
        stripped = line.strip()
        nested = re.match(r"(?:public\s+)?(?:sealed\s+)?(?:record|enum|class)\b",
                          stripped)
        if depth == 1 and not nested:
            m = re.match(
                r"(?:event\s+)?[\w<>?\[\],\s\.\(\)]+?\s(\w+)\s*(?:\(|=>|\{|;)",
                stripped)
            if m:
                names.append(m.group(1))
        depth += stripped.count("{") - stripped.count("}")
        if depth < 0:
            depth = 0
    out = []
    for n in names:
        if n not in out:
            out.append(n)
    return out


def main():
    iface = strip_comments(io.open(IFACE, encoding="utf-8").read())
    proxy = strip_comments(io.open(PROXY, encoding="utf-8").read())

    missing = []
    for name in interface_members(iface):
        if name in SELF_ANSWERED:
            continue
        # Forwarded means the proxy reaches the plugin for this member: through
        # the _inner field for properties and methods, or through the ctor's
        # own `inner` parameter, which is where events are subscribed.
        if not re.search(r"\b_?inner\.%s\b" % re.escape(name), proxy):
            missing.append(name)

    if missing:
        print("SafePluginProxy does not forward %d member(s):" % len(missing))
        for n in missing:
            print("   ", n)
        print()
        print("Each one silently resolves to the interface default, so the")
        print("plugin's answer never reaches the launcher. Forward it, or add")
        print("it to SELF_ANSWERED here with the reason.")
        return 1

    print("OK -- SafePluginProxy forwards every IGamePlugin member.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
