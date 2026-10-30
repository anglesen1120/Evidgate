#!/usr/bin/env python3
"""Runtime-base proof for scripts/build-images.sh.

A stage can only add on top of its FROM, so the pinned runtime base's layers must
be the built image's first layers. The comparison target is read from the registry
(the pinned index digest -> the platform's manifest -> its config -> rootfs.diff_ids)
rather than from the local store: with a multi-platform pin the local name resolves
to one platform image, so a name-based comparison silently compares the wrong
platform. Offline (`--offline`, or a registry that cannot be reached) the layer
proof degrades to the inherited config markers and says so.

usage: image-base-layers.py <reference> <platform> <pinned-runtime> <layers-json>
                            <env-json> <offline:0|1>
exit 0 = proven (or degraded with a printed warning), 1 = mismatch
"""
import json, sys, urllib.request

reference, platform, runtime = sys.argv[1], sys.argv[2], sys.argv[3]
layers, environment, offline = json.loads(sys.argv[4]), json.loads(sys.argv[5]), sys.argv[6] == "1"
architecture = platform.split("/")[1]

ACCEPT = ", ".join([
    "application/vnd.docker.distribution.manifest.list.v2+json",
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.v2+json",
])


def fetch(url):
    request = urllib.request.Request(url)
    request.add_header("Accept", ACCEPT)
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.loads(response.read().decode())


def base_layers():
    registry, _, rest = runtime.partition("/")
    image, _, index_digest = rest.partition("@")
    repository, _, _tag = image.partition(":")
    index = fetch("https://%s/v2/%s/manifests/%s" % (registry, repository, index_digest))
    entries = index.get("manifests")
    if not entries:
        raise RuntimeError("the pinned reference is not a manifest list: %s" % runtime)
    entry = next((m for m in entries
                  if m.get("platform", {}).get("os") == "linux"
                  and m["platform"].get("architecture") == architecture), None)
    if entry is None:
        raise RuntimeError("the pinned index %s has no linux/%s manifest" % (runtime, architecture))
    manifest = fetch("https://%s/v2/%s/manifests/%s" % (registry, repository, entry["digest"]))
    config = fetch("https://%s/v2/%s/blobs/%s" % (registry, repository, manifest["config"]["digest"]))
    return config["rootfs"]["diff_ids"], entry["digest"]


def marker_fallback(reason):
    tag = runtime.partition("@")[0].rsplit(":", 1)[-1]
    required = ["APP_UID=1654", "DOTNET_VERSION=" + tag.split("-")[0]]
    missing = [marker for marker in required if marker not in environment]
    if missing:
        sys.exit("build-images: cannot verify the runtime base of %s (%s) and the inherited "
                 "config markers %s are missing too" % (reference, reason, ", ".join(missing)))
    print("   runtime base: WARNING cannot reach the registry (%s); fell back to the inherited "
          "config markers %s — the layer proof was NOT performed" % (reason, ", ".join(required)))


if offline:
    marker_fallback("--offline")
else:
    try:
        base, manifest_digest = base_layers()
    except Exception as error:
        marker_fallback(error)
    else:
        if not base:
            sys.exit("build-images: the pinned runtime base reports no layers: %s" % runtime)
        if layers[:len(base)] != base:
            sys.exit("build-images: %s does not start with the pinned runtime layers\n"
                     "  pinned %s (linux/%s manifest %s)\n  base   %s\n  image  %s"
                     % (reference, runtime, architecture, manifest_digest, base, layers[:len(base)]))
        print("   runtime base: the pinned linux/%s manifest's %d layer(s) (%s) are the image's "
              "first layers" % (architecture, len(base), manifest_digest))
