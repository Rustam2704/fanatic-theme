# Translating the fanatic theme

All user-visible text of the bar, the sky, keyboard-switch and fan-tray lives in these files — one flat JSON per language:

| file | language |
|---|---|
| `en.json` | English (the fallback for any missing key) |
| `ua.json` | Ukrainian |
| `ru.json` | Russian |

## Add a language (no code change)

1. Copy `en.json` to `<code>.json` (e.g. `pl.json`, `de.json`). The file name is the language code.
2. Translate the **values** only. Never change a key (the part left of the colon).
3. Set `"lang.self"` to the language's own name (e.g. `"Polski"`) — this is what the language picker shows.
4. Put the file into `config\strings\` of the pack and install with `-Language <code>`, **or** on an installed machine copy it
   into every `strings\` folder under the install root (`fanatic-bar`, `sky-stars`, `keyboard-switch`, `fan-tray`), then
   right-click the desktop → sky settings → pick the language. The bar and the sky switch at once; keyboard-switch and
   fan-tray at their next start.

Missing keys fall back to English, so a partial translation still works.

## Rules (a translator — human or AI — must keep these)

- Keep placeholders exactly: `{0}`, `{1}`, `{2}` are filled in at runtime (numbers, names). You may move them inside the
  sentence, but every placeholder of the English value must stay.
- Keep `\n` (line break) where it is.
- Keys that start with `_` are comments for translators — leave them as they are (they are not shown anywhere).
- `lang.name.ua` / `lang.name.ru` are names of keyboard layouts in their own language (endonyms): do not translate.
- Keep the weather words short: they sit in a narrow forecast column (`weather.wmo.*` = WMO weather codes).
- `sky.const.*` are the 88 constellations: use the conventional astronomical name in your language.

### Date words use a pixel display font with a limited alphabet

These keys are drawn with the Diablo bitmap font on the taskbar and in the clock / calendar:
`date.week.*`, `date.week_abbr.*`, `clock.week_abbr.*`, `date.month.*`, `date.month_nom.*`, `date.month_abbr.*`.

The font has: Latin `A–Z`, digits, Cyrillic `А Б В Г Ґ Д Е Є Ж З И І Ї Й К Л М Н О П Р С Т У Ф Х Ц Ч Ш Щ Ь Ю Я`, and `space , . ' -`.
Text is shown in capitals, so case does not matter. **A letter outside this set is drawn as a blank** (e.g. `é`, `ü`, `ł`,
`Ы`, `Э`, `Ъ`, `Ё`). For these keys, write the plain letter instead (`e` for `é`) or pick a form without it.
(To add letters, put a TTF with them at `assets\fonts\DiabloCyr.ttf` and run `src\fanatic-bar\tools\build-cyr.ps1` with the
letters added to its character list, then rebuild the bar.)

`date.month.*` is the month as used in a date ("26 <month> 2026" — genitive in Slavic languages); `date.month_nom.*` is the
plain month name (calendar header). In English both are the same.
