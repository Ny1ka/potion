# Pinned sources for Potion's LGPL FFmpeg build. Sourced by build-macos.sh.
# Every archive is checked against its SHA-256 before use. Checksums were recorded on 2026-11-21 from the
# official download sites over HTTPS; the Xiph ones match Xiph's published SHA256SUMS files.
# Licenses: FFmpeg LGPL-2.1-or-later (built without --enable-gpl/--enable-nonfree), LAME LGPL-2.0-or-later,
# Opus/libogg/libvorbis/libvpx BSD-3-Clause. All are compatible with shipping FFmpeg as a separate program.

FFMPEG_VERSION=9.1
FFMPEG_URL="https://ffmpeg.org/releases/ffmpeg-${FFMPEG_VERSION}.tar.xz"
FFMPEG_SHA256=1a4d394ae63d190a526cd052eb4415c156b5c5e2df37efab41a7de0b8fde2433

LAME_VERSION=3.100
LAME_URL="https://downloads.sourceforge.net/project/lame/lame/${LAME_VERSION}/lame-${LAME_VERSION}.tar.gz"
LAME_SHA256=ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e

OPUS_VERSION=1.6.1
OPUS_URL="https://downloads.xiph.org/releases/opus/opus-${OPUS_VERSION}.tar.gz"
OPUS_SHA256=6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1

OGG_VERSION=1.3.6
OGG_URL="https://downloads.xiph.org/releases/ogg/libogg-${OGG_VERSION}.tar.xz"
OGG_SHA256=5c8253428e181840cd20d41f3ca16557a9cc04bad4a3d04cce84808677fa1061

VORBIS_VERSION=1.3.7
VORBIS_URL="https://downloads.xiph.org/releases/vorbis/libvorbis-${VORBIS_VERSION}.tar.xz"
VORBIS_SHA256=b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b

VPX_VERSION=1.17.0
VPX_URL="https://github.com/webmproject/libvpx/archive/refs/tags/v${VPX_VERSION}.tar.gz"
VPX_SHA256=1020f184046187baa2985dbde38e0691f49c44088bca7a1842b0236c6081dc0a

# libwebp (BSD-3-Clause): WebP image output. Checksum recorded 2026-11-21 from Google's release bucket.
WEBP_VERSION=1.6.0
WEBP_URL="https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-${WEBP_VERSION}.tar.gz"
WEBP_SHA256=e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564

# SVT-AV1 (BSD-3-Clause-Clear + AOMedia Patent License 1.0): AVIF output where the OS can't encode it.
# Checksum recorded 2026-11-21 from the GitLab release archive.
SVTAV1_VERSION=4.3.0
SVTAV1_URL="https://gitlab.com/AOMediaCodec/SVT-AV1/-/archive/v${SVTAV1_VERSION}/SVT-AV1-v${SVTAV1_VERSION}.tar.gz"
SVTAV1_SHA256=95ce8fc2381ba707e65ac8b9b2e165915bfbe9cd4d2f7b0f58d64ce31bd98f6d
