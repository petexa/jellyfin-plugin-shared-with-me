# Spike series media for jf-share-spike. Idempotent. Runs on the test host through jf.run_remote (prefixed with
# "set -euo pipefail"); only touches /opt/jf-share-spike/media/shows, which the container sees as /media/shows.
#
# Known state afterwards:
#   Spike Show (2010)  tvshow.nfo GB-15, Season 01: S01E01 and S01E02, each a 3 s clip with an episode NFO (no rating),
#                      S01E04 whose own episode NFO says <mpaa>GB-18</mpaa>, and S01E05 whose own NFO carries <tag>gore</tag>
#                      (C1: an episode's own rating and tags; there is deliberately no S01E03 at rest)
#   Spike Kids (2011)  tvshow.nfo GB-PG, Season 01: S01E01
# A stray S01E03 of Spike Show (test_share_series.py adds one) is removed, and a removed S01E01 is made again; a renamed
# S01E02 (test_tv_playlists.py) is put back.
# No <lockdata> in tvshow.nfo: 12.4 creates an episode added under a locked series locked too, so it never reads its NFO.
SHOWS=/opt/jf-share-spike/media/shows
FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg

clip() { # $1 container path of the .mp4, $2 tone frequency (so the clips differ)
  if [ ! -s "$SHOWS/${1#/media/shows/}" ]; then
    docker exec jf-share-spike "$FFMPEG" -loglevel error -y \
      -f lavfi -i "testsrc=duration=3:size=320x240:rate=25" -f lavfi -i "sine=frequency=$2:duration=3" \
      -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$1"
  fi
}

episode() { # $1 show folder, $2 season, $3 episode, $4 title, $5 tone, $6 extra NFO elements (optional)
  local dir="$SHOWS/$1/Season 0$2" base
  base="$(echo "$1" | sed 's/ ([0-9]*)$//') S0$2E0$3"
  mkdir -p "$dir"
  clip "/media/shows/$1/Season 0$2/$base.mp4" "$5"
  cat > "$dir/$base.nfo" <<NFO
<?xml version="1.0"?>
<episodedetails><title>$4</title><season>$2</season><episode>$3</episode>${6:-}</episodedetails>
NFO
}

show() { # $1 folder, $2 title, $3 year, $4 rating, $5 tvdb id
  mkdir -p "$SHOWS/$1"
  cat > "$SHOWS/$1/tvshow.nfo" <<NFO
<?xml version="1.0"?>
<tvshow><title>$2</title><year>$3</year><mpaa>$4</mpaa><uniqueid type="tvdb">$5</uniqueid></tvshow>
NFO
}

mkdir -p "$SHOWS"
# test_tv_playlists.py renames S01E02 to "... S01E02 Remastered": take that away, so S01E02 is made again below
rm -f "$SHOWS/Spike Show (2010)/Season 01/Spike Show S01E02 Remastered."*
show "Spike Show (2010)" "Spike Show" 2010 GB-15 990010
episode "Spike Show (2010)" 1 1 "Spike Show One" 440
episode "Spike Show (2010)" 1 2 "Spike Show Two" 550
episode "Spike Show (2010)" 1 4 "Spike Show Four" 880 "<mpaa>GB-18</mpaa>"
episode "Spike Show (2010)" 1 5 "Spike Show Five" 990 "<tag>gore</tag>"
rm -f "$SHOWS/Spike Show (2010)/Season 01/Spike Show S01E03."*
show "Spike Kids (2011)" "Spike Kids" 2011 GB-PG 990011
episode "Spike Kids (2011)" 1 1 "Spike Kids One" 660
find "$SHOWS" -type f | sort
