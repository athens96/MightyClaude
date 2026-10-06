# The help site

The user help for Mighty Claude, built into one page per language and published on R2 under `mightyclaude/help/<lang>/`.

| Path | What |
|---|---|
| `content/<lang>/*.md` | The pages. `ko` is the master; every other language mirrors it. |
| `content/<lang>/_site.json` | The page chrome: titles, sidebar labels, callout labels. |
| `shots.json` | The screenshots. CI captures them from the demo profile (`native-macos.yml`, artifact `MightyClaude-macos-help-shots`). |
| `site/` | Build output. Git-ignored; uploaded with `scripts/help/upload.sh`. |

## Build

```sh
node scripts/help/build.mjs --check                  # validate only
node scripts/help/build.mjs                          # pages; pictures not encoded before are placeholders
node scripts/help/build.mjs --shots ~/Downloads/MightyClaude-macos-help-shots   # with the CI pictures (needs cwebp)
scripts/help/upload.sh --dry-run                     # list what would be uploaded
```

`--shots` takes the unzipped artifact as it is. A picture missing for one language falls back to the Korean one.

## Writing a page

Front matter: `title`, `order` and `section`. `section` must be one of the `sections` in `shots.json` and match the file name.

| Syntax | Result |
|---|---|
| `## Heading {#id}` / `### Heading {#id}` | A heading. The id is required and is the same in every language. |
| `![[shot-id]]` or `![[shot-id\|caption]]` | A screenshot from `shots.json`, on its own line. It follows the page's light or dark theme. |
| `{{ui:locale.key}}` | The app's own label from `locales/<lang>.json`. Use it for every button, menu item, tab and toggle you name. Keys with `{placeholders}` are refused. |
| `{{kbd:⌘K}}`, `{{kbd:Ctrl+Shift+E}}` | Keycaps. |
| `> [!note]`, `> [!tip]`, `> [!warning]` | A callout. The following `>` lines are its text. |
| `{{platform:windows}}` | A small platform badge (`mac`, `windows`, `phone`). |
| `[text](layout.md#tabs)`, `[text](#id)` | A link to another page or heading. External links must be `http(s)://`. |

Lists (`-`, `1.`, nested by indentation), tables, `**bold**`, `` `code` `` and fenced code blocks work as usual.

In Korean, a particle right after `{{ui:…}}` must match the label's last syllable (을/를, 이/가, 은/는, 으로/로). When in doubt, put a noun after the label (`{{ui:…}} 단추를`, `{{ui:…}} 항목을`, `{{ui:…}} 탭에서`).

## Adding a language (zh, ja)

1. Copy `content/ko/` to `content/<lang>/`.
2. Translate the text and `_site.json`. Keep every heading `{#id}`, every `![[shot]]` and every `{{ui:…}}` key; the build fails when they differ from `ko`. Word order around a `{{ui:…}}` may change.
3. Run `node scripts/help/build.mjs --check`.

The language switcher lists all four languages. A language without a content folder is shown greyed out.
