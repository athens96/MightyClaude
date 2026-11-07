# dmgbuild settings for the Mighty Claude install window (scripts/notarize-macos.sh).
#
#   dmgbuild -s scripts/dmg/settings.py -D app=<MightyClaude.app> -D background=<tiff> "Mighty Claude" <out.dmg>
#
# A 660×400 window with no toolbar, sidebar or status bar; the app on the left and an
# Applications link on the right at the positions scripts/dmg/make-background.swift
# draws its arrow between. The volume shows the app's own icon.
import os.path

application = defines["app"]  # noqa: F821 — dmgbuild passes -D values in `defines`
appname = os.path.basename(application)

format = "UDZO"
filesystem = "HFS+"
files = [application]
symlinks = {"Applications": "/Applications"}
icon = os.path.join(application, "Contents", "Resources", "MightyClaude.icns")
badge_icon = None

background = defines["background"]  # noqa: F821
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
sidebar_width = 0
window_rect = ((200, 120), (660, 400))
default_view = "icon-view"
show_icon_preview = False
include_icon_view_settings = True
include_list_view_settings = False

arrange_by = None
grid_offset = (0, 0)
grid_spacing = 100
scroll_position = (0, 0)
label_pos = "bottom"
text_size = 13
icon_size = 128
icon_locations = {appname: (165, 180), "Applications": (495, 180)}
