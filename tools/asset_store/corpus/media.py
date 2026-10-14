"""Manifest and inert-install cases for PR41's three future media consumers."""
from copy import deepcopy


def media_cases():
    cases = []

    def add(name, component, accepted=True, why="Typed media metadata", payload=None, zipped=False):
        case = dict(name=name, expect="accept" if accepted else "reject", why=why,
                    manifest=dict(schema="guo/store-pack@2", kind="content", target="client",
                                  dependencies={}, components=[deepcopy(component)]),
                    payload=payload or {"still.png": "png", "cloud.ply": "ply\n", "voice.json": "{}", "hello.wav": "wav"})
        # PLY is allowed only when a splat component actually declares it.
        if component["type"] != "splat" and not component["entry"].lower().endswith(".ply"):
            case["payload"].pop("cloud.ply", None)
        if zipped:
            case["zip"] = {}
        cases.append(case)

    hd = dict(id="art", type="hd-art", target="client", entry="still.png",
              asset="static", graphic=3701, scale=2, native_w=44, native_h=60)
    splat = dict(id="cloud", type="splat", target="client", entry="cloud.ply", graphic=3701)
    voice = dict(id="speech", type="voice", target="client", entry="voice.json", body=400, wav=["hello.wav"])
    for label, component in [("hd", hd), ("splat", splat), ("voice", voice)]:
        add("media-" + label + "-install", component, zipped=True,
            why="Install accepts declared media inertly without a runtime consumer")
        for field, value in [("target", "shared"), ("entry", "missing.png"), ("execute", True)]:
            changed = dict(component, **{field: value})
            add(f"media-{label}-{field}", changed, False)
    for label, field, value in [("scale-zero", "scale", 0), ("scale-bool", "scale", True),
                                ("scale-string", "scale", "2"), ("scale-big", "scale", 17),
                                ("width-fraction", "native_w", 1.5), ("width-zero", "native_w", 0),
                                ("height-big", "native_h", 4097), ("graphic-negative", "graphic", -1),
                                ("graphic-big", "graphic", 65536), ("graphic-bool", "graphic", True),
                                ("asset-unknown", "asset", "gump"), ("entry-json", "entry", "voice.json"),
                                ("foreign-body", "body", 400), ("missing-scale", "scale", None)]:
        add("media-hd-" + label, dict(hd, **{field: value}), False)
    add("media-hd-land-square", dict(hd, asset="land", native_h=44))
    add("media-hd-land-rectangle", dict(hd, asset="land"), False)
    add("media-hd-fraction-scale", dict(hd, scale=1.5))
    add("media-hd-upper-png", dict(hd, entry="image.PNG"), payload={"still.png": "png", "image.PNG": "png"})
    for label, field, value in [("graphic-missing", "graphic", None), ("graphic-string", "graphic", "12"),
                                ("graphic-big", "graphic", 65536), ("entry-png", "entry", "still.png"),
                                ("foreign-scale", "scale", 2)]:
        add("media-splat-" + label, dict(splat, **{field: value}), False)
    add("media-splat-undeclared-ply", splat, False, payload={"still.png": "png", "cloud.ply": "ply", "hidden.ply": "ply"})
    add("media-splat-uppercase", dict(splat, entry="cloud.PLY"), payload={"still.png": "png", "cloud.PLY": "ply"})
    named = dict(voice, name="Town crier"); named.pop("body")
    add("media-voice-named", named)
    for label, field, value in [("two-bindings", "name", "Town crier"), ("no-binding", "body", None),
                                ("body-big", "body", 65536), ("body-bool", "body", True),
                                ("no-wav", "wav", []), ("missing-wav", "wav", ["missing.wav"]),
                                ("wrong-wav", "wav", ["still.png"]), ("duplicate-wav", "wav", ["hello.wav", "hello.wav"]),
                                ("null-wav", "wav", [None]), ("entry-wav", "entry", "hello.wav"),
                                ("foreign-graphic", "graphic", 123)]:
        add("media-voice-" + label, dict(voice, **{field: value}), False)
    for label, value in [("empty", " "), ("long", "x" * 101), ("control", "NPC\nname"), ("number", 123)]:
        add("media-voice-name-" + label, dict(named, name=value), False)
    add("media-foreign-hd-field", dict(id="base", type="static", target="client", entry="still.png", scale=2), False)
    add("media-ply-wrong-type", dict(id="base", type="static", target="client", entry="cloud.ply"), False)
    cases.append(dict(name="media-ply-v1", expect="reject", why="PLY cannot sneak into v1 presentation packs",
                      payload={"still.png": "png", "cloud.ply": "ply"}))
    return cases
